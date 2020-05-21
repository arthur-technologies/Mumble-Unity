using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MumbleProto;
using ProtoBuf;
using System.Timers;
using System.Threading;
using System.Text;
using ProtoBuf.Meta;
using Debug = UnityEngine.Debug;
using Version = MumbleProto.Version;

namespace Mumble
{
    public class MumbleTcpConnection
    {
        private readonly UpdateOcbServerNonce _updateOcbServerNonce;
        private readonly IPEndPoint _host;
        private readonly string _hostname;
        private readonly MumbleClient _mumbleClient;
        private readonly TcpClient _tcpClient;
        private BinaryReader _reader;
        private SslStream _ssl;
        private MumbleUdpConnection _udpConnection;
        private bool _validConnection;
        private BinaryWriter _writer;
        private System.Timers.Timer _tcpTimer;
        private Thread _processThread;
        private string _username;
        private string _password;
        private NetworkStream networkStream;
        private TypeModel MyProto;

        internal MumbleTcpConnection(IPEndPoint host, string hostname, UpdateOcbServerNonce updateOcbServerNonce,
            MumbleUdpConnection udpConnection, MumbleClient mumbleClient)
        {
            _host = host;
            _hostname = hostname;
            _mumbleClient = mumbleClient;
            _udpConnection = udpConnection;
            _tcpClient = new TcpClient();
            _updateOcbServerNonce = updateOcbServerNonce;
            
            // _processThread = new Thread(ProcessTcpData)
            // {
            //     IsBackground = true
            // };
        }

        internal void StartClient(string username, string password, Action<bool> onConnected)
        {
            _username = username;
            _password = password;
            
            ConnectTCP((onConnected));
            /*_tcpClient.BeginConnect(_host.Address, _host.Port, ar =>
            {
                OnTcpConnected(onConnected);
                //onConnected(OnTcpConnected(ar));
            }, null);*/
            //Debug.Log("Attempting to connect to " + _host);
        }
        private void ConnectTCP(Action<bool> onConnected)
        {
            try
            {
                if (_tcpClient.Connected)
                {
                    onConnected(true);
                    return;
                }
                _tcpClient.Connect(_host.Address,_host.Port);
                if (!_tcpClient.Connected)
                {
                    Debug.LogError("Connection failed! Please confirm that you have internet access, and that the hostname is correct");
                    // _mumbleClient?.OnConnectionDisconnect();
                    throw new Exception("Failed to connect");
                }
            } 
            catch (SocketException e)
            {
                Debug.LogError("Connection failed! Please confirm that you have internet access, and that the hostname is correct- > Exception caught: ");
                // _mumbleClient?.OnConnectionDisconnect();
                onConnected(false);
                throw;
                //return;
            }
            

            MyProto = (TypeModel) Activator.CreateInstance(Type.GetType("MyProtoModel, MyProtoModel") ?? throw new Exception("Failed to Create MyProtoModel Serailizer/Deserializer"));
            try
            { 
                networkStream = _tcpClient.GetStream();
                _ssl = new SslStream(networkStream, false, ValidateCertificate);
                _ssl.AuthenticateAsClient(_hostname);
                _reader = new BinaryReader(_ssl);
                _writer = new BinaryWriter(_ssl);
                onConnected(true);
            }
            catch (Exception e)
            {
                //Console.WriteLine(e);
                onConnected(false);
                throw;
            }

            DateTime startWait = DateTime.Now;
            while (!_ssl.IsAuthenticated)
            {
                if (DateTime.Now - startWait > TimeSpan.FromSeconds(2))
                    throw new TimeoutException("Time out waiting for SSL authentication");
            }
            SendVersion();
            //StartPingTimer();
        }
        private void SendVersion()
        {
            var version = new Version
            {
                Release = MumbleClient.ReleaseName,
                version = (MumbleClient.Major << 16) | (MumbleClient.Minor << 8) | (MumbleClient.Patch),
                Os = Environment.OSVersion.ToString(),
                OsVersion = Environment.OSVersion.VersionString,
            };
            SendMessage(MessageType.Version, version);
        }
        private void StartPingTimer()
        {
            // Keepalive, if the Mumble server doesn't get a message 
            // for 30 seconds it will close the connection
            _tcpTimer = new System.Timers.Timer(MumbleConstants.PING_INTERVAL_MS);
            _tcpTimer.Elapsed += SendPing;
            _tcpTimer.Enabled = true; 
            // _processThread.Start();
        }

        internal void SendMessage<T>(MessageType mt, T message)
        {
            try
            {


                lock (_ssl)
                {
                    //if (mt != MessageType.Ping && mt != MessageType.UDPTunnel)
                    //Debug.Log("Sending " + mt + " message");
                    //_writer.Write(IPAddress.HostToNetworkOrder((Int16) mt));
                    //Serializer.SerializeWithLengthPrefix(_ssl, message, PrefixStyle.Fixed32BigEndian);
                    Int16 messageType = (Int16) mt;

                    // UDP Tunnels have their own way in which they handle serialization
                    if (mt == MessageType.UDPTunnel)
                    {
                        UDPTunnel udpTunnel = message as UDPTunnel;
                        Int32 messageSize = (Int32) udpTunnel.Packet.Length;
                        _writer.Write(IPAddress.HostToNetworkOrder(messageType));
                        _writer.Write(IPAddress.HostToNetworkOrder(messageSize));
                        _writer.Write(udpTunnel.Packet);
                    }
                    else
                    {
                        MemoryStream messageStream = new MemoryStream();
                        MyProto.Serialize(messageStream, message);
                        //Serializer.NonGeneric.Serialize(messageStream, message);
                        Int32 messageSize = (Int32) messageStream.Length;
                        _writer.Write(IPAddress.HostToNetworkOrder(messageType));
                        _writer.Write(IPAddress.HostToNetworkOrder(messageSize));
                        messageStream.Position = 0;
                        _writer.Write(messageStream.ToArray());
                    }

                    /*
                    StringBuilder sb = new StringBuilder();
                    byte[] msgArray = messageStream.ToArray();
                    for (int i = 0; i < msgArray.Length; i++)
                    {
                        sb.Append(msgArray[i]);
                        sb.Append(",");
                    }
                    Debug.Log(sb.ToString());
                    */
                    _writer.Flush();
                }
            }
            catch
            {
                // 
                throw;
            }
        }

        //TODO implement actual certificate validation
        private bool ValidateCertificate(object sender, X509Certificate certificate, X509Chain chain,
            SslPolicyErrors errors)
        {
            return true;
        }

        public bool ProcessTcpData()
        {
            try
            {
                var isConnected = !_tcpClient.Connected;
            }
            catch (Exception e)
            {
                
                throw;
            }

            try
            {
                // if (networkStream == null)
                // {
                //     return false;
                // }
                if (!networkStream.DataAvailable)
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                throw;
            }
            
            // This thread is aborted in Close()
            // try
            // {
            lock (_ssl)
            {
                //Debug.Log("Processing data of type: " + messageType);
                try
                {
                    var messageType = (MessageType) IPAddress.NetworkToHostOrder(_reader.ReadInt16());
                    switch (messageType)
                    {
                        case MessageType.Version:
                            _mumbleClient.RemoteVersion = (Version) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(Version),
                                PrefixStyle.Fixed32BigEndian, 0);
                            //Debug.Log("Server version: " + _mc.RemoteVersion.release);
                            var authenticate = new Authenticate
                            {
                                Username = _username,
                                Password = _password,
                                Opus = true
                            };
                            SendMessage(MessageType.Authenticate, authenticate);
                           // ProcessTcpData();
                            break;
                        case MessageType.CryptSetup:
                            var cryptSetup = (CryptSetup) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(CryptSetup),
                                PrefixStyle.Fixed32BigEndian, 0);
                            ProcessCryptSetup(cryptSetup);
                            _validConnection = true;
                            SendPing();
                            //ProcessTcpData();
                            //Debug.Log("Got crypt");
                            break;
                        case MessageType.CodecVersion:
                            _mumbleClient.CodecVersion = (CodecVersion) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(CodecVersion),
                                PrefixStyle.Fixed32BigEndian, 0);
                            //Debug.Log("Got codec version");
                            break;
                        case MessageType.ChannelState:
                            ChannelState ChannelState = (ChannelState) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(ChannelState),
                                PrefixStyle.Fixed32BigEndian, 0);
                            /*
                            Debug.Log("Channel state Name = " + ChannelState.name);
                            Debug.Log("Channel state ID = " + ChannelState.channel_id);
                            Debug.Log("Channel state Position = " + ChannelState.position);
                            Debug.Log("Channel state Temporary = " + ChannelState.temporary);
                            Debug.Log("Channel state Parent = " + ChannelState.parent);
                            Debug.Log("Channel state Description = " + ChannelState.description);
                            */
                            _mumbleClient.AddChannel(ChannelState);
                            break;
                        case MessageType.PermissionQuery:
                            _mumbleClient.PermissionQuery = (PermissionQuery) MyProto.DeserializeWithLengthPrefix(_ssl,
                                null, typeof(PermissionQuery),
                                PrefixStyle.Fixed32BigEndian, 0);
                            //Debug.Log("Permission Query = " + _mumbleClient.PermissionQuery.permissions);
                            //Debug.Log("Permission Channel = " + _mumbleClient.PermissionQuery.channel_id);
                            break;
                        case MessageType.UserState:
                            //This is called for every user in the room, including us
                            UserState user = (UserState) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(UserState),
                                PrefixStyle.Fixed32BigEndian, 0);

                            //Debug.Log("Name: " + user.Name);
                            //Debug.Log("Session: " + user.Session);
                            //Debug.Log("actor: " + user.Actor);
                            //Debug.Log("Chan: " + user.ChannelId);
                            //Debug.Log("ID: " + user.UserId);
                            _mumbleClient.AddOrUpdateUser(user);
                            break;
                        case MessageType.ServerSync:
                            //This is where we get our session Id
                            //Debug.Log("Will server sync!");
                            _mumbleClient.SetServerSync((ServerSync) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(ServerSync),
                                PrefixStyle.Fixed32BigEndian, 0));
                            //Debug.Log("Server Sync Session= " + _mumbleClient.ServerSync.session);
                            break;
                        case MessageType.ServerConfig:
                            _mumbleClient.ServerConfig = (ServerConfig) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(ServerConfig),
                                PrefixStyle.Fixed32BigEndian, 0);
                            //Debug.Log("Sever config = " + _mumbleClient.ServerConfig);
                            Debug.Log("Mumble is Connected");
                            _mumbleClient.OnConnectionConnected();
                            _validConnection = true; // handshake complete
                            break;
                        case MessageType.SuggestConfig:
                            //Contains suggested configuratio options from the server
                            //like whether to send positional data, client version, etc.
                            MyProto.DeserializeWithLengthPrefix(_ssl, null, typeof(SuggestConfig),
                                PrefixStyle.Fixed32BigEndian, 0);
                            break;
                        case MessageType.TextMessage:
                            TextMessage textMessage = (TextMessage) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(TextMessage),
                                PrefixStyle.Fixed32BigEndian, 0);

                            Debug.Log("Text message = " + textMessage.Message);
                            Debug.Log("Text actor = " + textMessage.Actor);
                            //Debug.Log("Text channel = " + textMessage.channel_id[0]);
                            //Debug.Log("Text session Length = " + textMessage.Sessions.Length);
                            //Debug.Log("Text Tree Length = " + textMessage.TreeIds.Length);
                            break;
                        case MessageType.UDPTunnel:
                            var length = IPAddress.NetworkToHostOrder(_reader.ReadInt32());
                            Debug.Log("Received UDP tunnel of length: " + length);
                            //At this point the message is already decrypted
                            _udpConnection.UnpackOpusVoicePacket(_reader.ReadBytes(length), false);
                            /*
                            //var udpTunnel = Serializer.DeserializeWithLengthPrefix<UDPTunnel>(_ssl,
                                PrefixStyle.Fixed32BigEndian);
                            */
                            break;
                        case MessageType.Ping:
                            Ping ping = (Ping) MyProto.DeserializeWithLengthPrefix(_ssl, null, typeof(MumbleProto.Ping),
                                PrefixStyle.Fixed32BigEndian, 0);
                            ReceivePing(ping);
                            break;
                        case MessageType.Reject:
                            // This is called, for example, when the max number of users has been hit
                            var reject = (Reject) MyProto.DeserializeWithLengthPrefix(_ssl, null, typeof(Reject),
                                PrefixStyle.Fixed32BigEndian, 0);
                            _validConnection = false;
                            Debug.LogError("Mumble server reject: " + reject.Reason);
                            // _mumbleClient.OnConnectionDisconnect();
                            // The server connection is over, so we return
                            return false;
                        case MessageType.UserRemove:
                            var removal = (UserRemove) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(UserRemove),
                                PrefixStyle.Fixed32BigEndian, 0);
                            Debug.Log("Removing " + removal.Session);
                            _mumbleClient.RemoveUser(removal.Session);
                            break;
                        case MessageType.ChannelRemove:
                            var removedChan = (ChannelRemove) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(ChannelRemove),
                                PrefixStyle.Fixed32BigEndian, 0);
                            _mumbleClient.RemoveChannel(removedChan.ChannelId);
                            Debug.Log("Removing channel " + removedChan.ChannelId);
                            break;
                        case MessageType.PermissionDenied:
                            var denial = (PermissionDenied) MyProto.DeserializeWithLengthPrefix(_ssl, null,
                                typeof(PermissionDenied),
                                PrefixStyle.Fixed32BigEndian, 0);
                            Debug.LogError("Permission denied with fields Name: " + denial.Name + ", Type: " +
                                           denial.Type +
                                           ", Reason: " + denial.Reason);
                            break;
                        case MessageType.Authenticate:
                        case MessageType.ContextActionModify:
                        case MessageType.RequestBlob:
                        case MessageType.VoiceTarget:
                        default:
                            throw new NotImplementedException(
                                $"{nameof(Process)} {nameof(MessageType)}.{messageType.ToString()}");
                        // default:
                        //     Debug.LogError("Message type " + messageType + " not implemented");
                        //     break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("TCP: "+ex.Message +ex.StackTrace);
                    throw;
                }
            }

            return true;
        }

        private void ProcessCryptSetup(CryptSetup cryptSetup)
        {
            if (cryptSetup.Key != null && cryptSetup.ClientNonce != null && cryptSetup.ServerNonce != null)
            {
                // Apply the key and client/server nonce values provided
                _mumbleClient.CryptSetup = cryptSetup;
                _mumbleClient.ConnectUdp();
            }
            else if(cryptSetup.ServerNonce != null)
            {
                Debug.Log("Updating server nonce");
                _updateOcbServerNonce(cryptSetup.ServerNonce);
            }
            else
            {
                // This generally means that the server is requesting our nonce
                SendMessage(MessageType.CryptSetup, new CryptSetup { ClientNonce = _mumbleClient.GetLatestClientNonce() });
            }
        }

        internal void Close()
        {
            if(_ssl != null)
                _ssl.Close();
            _ssl = null;
            if(_tcpTimer != null)
                _tcpTimer.Close();
            _tcpTimer = null;
            if(_processThread != null)
                _processThread.Abort();
            _processThread = null;
            if(_reader != null)
                _reader.Close();
            _reader = null;
            if(_writer != null)
                _writer.Close();
            _writer = null;
            if(_tcpClient != null)
                _tcpClient.Close();
        }

        internal void SendPing(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            if (_validConnection)
            {
                var ping = new MumbleProto.Ping();
                ping.Timestamp = (ulong) (DateTime.UtcNow.Ticks - DateTime.Parse("01/01/1970 00:00:00").Ticks);
                //Debug.Log("Sending ping");
                SendMessage(MessageType.Ping, new MumbleProto.Ping());
            }
        }
        
        #region pings
        //using the approch described here to do running calculations of ping values.
        // http://dsp.stackexchange.com/questions/811/determining-the-mean-and-standard-deviation-in-real-time
        private float _meanOfPings;
        private float _varianceTimesCountOfPings;
        private int _countOfPings;
        
        public float? TcpPingAverage { get; set; }
        public float? TcpPingVariance { get; set; }
        public uint? TcpPingPackets { get; set; }

        /// <summary>
        /// Gets a value indicating whether ping stats should set timestamp when pinging.
        /// Only set the timestamp if we're currently connected.  This prevents the ping stats from being built.
        /// otherwise the stats will be throw off by the time it takes to connect.
        /// </summary>
        /// <value>
        ///   <c>true</c> if ping stats should set timestamp when pinging; otherwise, <c>false</c>.
        /// </value>
        internal bool ShouldSetTimestampWhenPinging { get; private set; }

        internal void ReceivePing(Ping ping)
        {
            ShouldSetTimestampWhenPinging = true;
            if (ping.ShouldSerializeTimestamp() && ping.Timestamp != 0)
            {
                var mostRecentPingtime =
                    (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - (long)ping.Timestamp).TotalMilliseconds;

                //The ping time is the one-way transit time.
                mostRecentPingtime /= 2;

                var previousMean = _meanOfPings;
                _countOfPings++;
                _meanOfPings = _meanOfPings + ((mostRecentPingtime - _meanOfPings) / _countOfPings);
                _varianceTimesCountOfPings = _varianceTimesCountOfPings +
                                             ((mostRecentPingtime - _meanOfPings) * (mostRecentPingtime - previousMean));

                TcpPingPackets = (uint)_countOfPings;
                TcpPingAverage = _meanOfPings;
                TcpPingVariance = _varianceTimesCountOfPings / _countOfPings;
            }


        }
        
        
        internal void SendPing()
        {
            if (_validConnection)
            {
                var ping = new Ping();

                //Only set the timestamp if we're currently connected.  This prevents the ping stats from being built.
                //  otherwise the stats will be throw off by the time it takes to connect.
                if (ShouldSetTimestampWhenPinging)
                {
                    ping.Timestamp = (ulong)DateTime.UtcNow.Ticks;
                }

                if (TcpPingAverage.HasValue)
                {
                    ping.TcpPingAvg = TcpPingAverage.Value;
                }
                if (TcpPingVariance.HasValue)
                {
                    ping.TcpPingVar = TcpPingVariance.Value;
                }
                if (TcpPingPackets.HasValue)
                {
                    ping.TcpPackets = TcpPingPackets.Value;
                }

                lock (_ssl)
                    SendMessage(MessageType.Ping, ping);
                //Send<Ping>(PacketType.Ping, ping);
            }
        }

        
        #endregion
        
    }
}