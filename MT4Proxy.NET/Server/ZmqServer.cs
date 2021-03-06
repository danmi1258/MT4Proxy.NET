﻿using NLog.Internal;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using MT4CliWrapper;
using NLog;
using Newtonsoft.Json;
using Castle.Zmq;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Diagnostics;
using System.Linq;

namespace MT4Proxy.NET.Core
{
    class ZmqServer : ConfigBase, IInputOutput, IDisposable, IServer
    {
        internal override void LoadConfig(NLog.Internal.ConfigurationManager aConfig)
        {
            ZmqBindAddr = aConfig.AppSettings["zmq_bind"];
            ZmqPubBindAddr = aConfig.AppSettings["zmq_pub_bind"];
        }

        private static string ZmqBindAddr
        { get; set; }

        private static string ZmqPubBindAddr
        {
            get;
            set;
        }
        public void Initialize()
        {
            EnableRunning = true;
            Init();
        }

        public void Stop()
        {
            try
            {
                if(_respSockets != null)
                {
                    foreach(var i in _respSockets)
                        i.Dispose();
                    _respSockets = null;
                }
            }
            catch(Exception e)
            {
                Utils.CommonLog.Error("关闭ZMQ监听套接字出现问题,{0}", e.Message);
            }
            try
            {
                if (_pubSocket != null)
                {
                    _pubSocket.Dispose();
                    _pubSocket = null;
                }
            }
            catch (Exception e)
            {
                Utils.CommonLog.Error("关闭Pub监听套接字出现问题,{0}", e.Message);
            }
            EnableRunning = false;
            ServerContainer.FinishStop();
        }

        private static bool EnableRunning = false;
        private Context _zmqCtx = null;
        private ConcurrentDictionary<string, Type> _apiDict = 
            new ConcurrentDictionary<string, Type>();
        private ConcurrentDictionary<string, MT4ServiceAttribute> _attrDict =
            new ConcurrentDictionary<string, MT4ServiceAttribute>();
        private static IZmqSocket _publisher = null;
        private static Semaphore _pubSignal = new Semaphore(0, 20000);
        private static ConcurrentQueue<Tuple<string, string>>
            _queMessages = new ConcurrentQueue<Tuple<string, string>>();
        private static IEnumerable<IZmqSocket> _respSockets = null;
        private static IZmqSocket _pubSocket = null;

        private int _mt4ID = 0;

        public ZmqServer()
        {

        }

        public ZmqServer(int aMT4ID, bool aEnableMT4Object = true)
        {
            if (!aEnableMT4Object) return;
            if (aMT4ID > 0)
                MT4 = Poll.Fetch(aMT4ID);
            else
                MT4 = Poll.New();
            _mt4ID = aMT4ID;
        }

        internal static void PubMessage(string aTopic, string aMessage)
        {
            var socket = _publisher;
            if(socket != null)
            {
                _queMessages.Enqueue(new Tuple<string, string>(aTopic, aMessage));
                _pubSignal.Release();
            }
        }

        private static void PubProc(object aArg)
        {
            while(EnableRunning)
            {
                _pubSignal.WaitOne();
                Tuple<string, string> item = null;
                _queMessages.TryDequeue(out item);
                if (item == null) continue;
                var topic = item.Item1;
                var message = item.Item2;
                var socket = _publisher;
                if (socket != null)
                {
                    socket.Send(topic, null, hasMoreToSend: true);
                    socket.Send(message);
                }
            }
        }

        public void Init()
        {
            var initlogger = LogManager.GetLogger("common");
            
            _zmqCtx = new Context();
            
            foreach (var i in Utils.GetTypesWithServiceAttribute())
            {
                var attr = i.GetCustomAttribute(typeof(MT4ServiceAttribute)) as MT4ServiceAttribute;
                var service = i;
                if (attr.EnableZMQ)
                {
                    var serviceName = string.Empty;
                    if (!string.IsNullOrWhiteSpace(attr.ZmqApiName))
                        serviceName = attr.ZmqApiName;
                    else
                        serviceName = service.Name;
                    initlogger.Info(string.Format("准备初始化ZMQ服务:{0}", serviceName));
                    _apiDict[serviceName] = i;
                    _attrDict[serviceName] = attr;
                }
            }
            var pubSocket = _zmqCtx.CreateSocket(SocketType.Pub);
            _pubSocket = pubSocket;
            var proto_items = ZmqBindAddr.Split(':');
            var port_range = proto_items[2].Split('-').Select
                (i => int.Parse(i.Trim())).ToArray();
            if(port_range[0] > port_range[1] || port_range[1] - port_range[0] > 100)
            {
                throw new Exception("端口范围设定有bug");
            }
            pubSocket.Bind(ZmqPubBindAddr);
            _publisher = pubSocket;
            var th_pub = new Thread(PubProc);
            th_pub.IsBackground = true;
            th_pub.Start();
            var lstSockets = new List<IZmqSocket>();
            for (int i = port_range[0]; i <= port_range[1]; i++)
            {
                var addr = string.Format("{0}:{1}:{2}", proto_items[0], proto_items[1], i);
                lstSockets.Add(StartLink(addr));
            }
        }

        private IZmqSocket StartLink(string addr)
        {
            var sock = _zmqCtx.CreateSocket(SocketType.Rep);
            sock.Bind(addr);
            var jss = new JavaScriptSerializer();
            var polling = new Polling(PollingEvents.RecvReady, sock);
            var watch = new Stopwatch();
            polling.RecvReady += (socket) =>
            {
                try
                {
                    var item = socket.RecvString(Encoding.UTF8);
                    var dict = jss.Deserialize<dynamic>(item);
                    string api_name = dict["__api"];
                    var mt4_id = 0;
                    if (dict.ContainsKey("mt4UserID"))
                        mt4_id = Convert.ToInt32(dict["mt4UserID"]);
                    if (_apiDict.ContainsKey(api_name))
                    {
                        var service = _apiDict[api_name];
                        var serviceobj = Activator.CreateInstance(service) as IService;
                        using (var server = new ZmqServer(mt4_id, !_attrDict[api_name].DisableMT4))
                        {
                            server.Logger = LogManager.GetLogger("common");
                            if (_attrDict[api_name].ShowRequest)
                                server.Logger.Info(string.Format("ZMQ,recv request:{0}", item));
                            watch.Restart();
                            serviceobj.OnRequest(server, dict);
                            if (server.Output != null)
                            {
                                socket.Send(server.Output);
                                watch.Stop();
                                var elsp = watch.ElapsedMilliseconds;
                                if (_attrDict[api_name].ShowResponse)
                                    server.Logger.Info(string.Format("ZMQ[{0}ms] response:{1}",
                                        elsp, server.Output));
                            }
                            else
                            {
                                socket.Send(string.Empty);
                                watch.Stop();
                                var elsp = watch.ElapsedMilliseconds;
                                if (_attrDict[api_name].ShowResponse)
                                    server.Logger.Warn(string.Format("ZMQ[{0}ms] response empty,source:{1}",
                                        elsp, item));
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    var logger = LogManager.GetLogger("clr_error");
                    logger.Error("处理单个ZMQ请求失败,{0},{1}", e.Message, e.StackTrace);
                    socket.Send(string.Empty);
                }
                finally
                {
                    if (EnableRunning)
                        ContinuePoll(polling);
                }
            };
            if (EnableRunning)
                ContinuePoll(polling);
            return sock;
        }

        private static void ContinuePoll(Polling polling)
        {
            Task.Factory.StartNew(() => 
            {
                try
                {
                    polling.PollForever();
                }
                catch
                { }
            });
        }

        public void Pub(string aChannel, string aJson)
        {

        }

        public string Output
        {
            get;
            set;
        }

        public MT4Wrapper MT4
        {
            get;
            private set;
        }
        public Logger Logger
        {
            get;
            private set;
        }
        public string RedisOutputList
        {
            get;
            set;
        }

        bool disposed = false;
        public void Dispose()
        {
            Dispose(true);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
            {
                if (MT4 != null)
                    if (_mt4ID > 0)
                        Poll.Bringback(MT4);
                    else
                        Poll.Release(MT4);
                MT4 = null;
                Logger = null;
            }
            disposed = true;
        }
    }
}
