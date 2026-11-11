using System;
using System.Net;
using System.Net.Sockets;
using GameShared;

namespace GameServer
{
    class Client
    {
        public int id;
        public TCP tcp;
        public UDP udp;

        public Client(int _clientId)
        {
            id = _clientId;
            tcp = new TCP(id);
            udp = new UDP(id);
        }

        // [HEARTBEAT] Ngắt kết nối hoàn chỉnh. Luôn gọi trên luồng game (qua ThreadManager.EnqueueAction).
        public void Disconnect()
        {
            if (!tcp.Disconnect()) return; // đã ngắt rồi, không làm lại lần hai

            Console.WriteLine($"[NGAT KET NOI] Client {id} da roi khoi may chu.");

            udp.Disconnect();

            // TODO: dọn player và báo cho người chơi khác (cần xem ServerHandle / Player / ServerSend)
            // ví dụ: player = null; ServerSend.PlayerDisconnected(id);
        }

        public class TCP
        {
            public TcpClient socket = null!;
            private readonly int id;
            private NetworkStream stream = null!;
            private byte[] receiveBuffer = null!;
            private readonly object disconnectLock = new object(); // [HEARTBEAT]

            public TCP(int _id)
            {
                id = _id;
            }

            public void Connect(TcpClient _socket)
            {
                socket = _socket;
                socket.ReceiveBufferSize = Constants.DATA_BUFFER_SIZE;
                socket.SendBufferSize = Constants.DATA_BUFFER_SIZE;

                stream = socket.GetStream();
                receiveBuffer = new byte[Constants.DATA_BUFFER_SIZE];

                stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);

                ServerSend.Welcome(id, "Chao mung ban den voi may chu Tiny Bomber!");
            }

            // [HEARTBEAT] Trả về true nếu lần gọi này thực sự đóng socket
            public bool Disconnect()
            {
                lock (disconnectLock)
                {
                    if (socket == null) return false;

                    try { socket.Close(); } catch { }
                    socket = null!;   // quan trọng: Server dùng socket == null để biết slot trống
                    return true;
                }
            }

            public void SendData(Packet _packet)
            {
                try
                {
                    if (socket != null)
                    {
                        stream.BeginWrite(_packet.ToArray(), 0, _packet.Length(), null, null);
                    }
                }
                catch (Exception _ex)
                {
                    Console.WriteLine($"Loi khi gui du lieu toi Player {id}: {_ex}");
                }
            }

            private void ReceiveCallback(IAsyncResult _result)
            {
                try
                {
                    int _byteLength = stream.EndRead(_result);
                    if (_byteLength <= 0)
                    {
                        ThreadManager.EnqueueAction(() => Server.clients[id].Disconnect());
                        return;
                    }

                    Server.Touch(id);

                    byte[] _data = new byte[_byteLength];
                    Array.Copy(receiveBuffer, _data, _byteLength);

                    using (Packet _packet = new Packet(_data))
                    {
                        _packet.DecryptPayload();               // 1. giải mã TRƯỚC

                        int _packetLength = _packet.ReadInt();  // 2. rồi mới đọc length
                        int _packetId = _packet.ReadInt();      //    và id

                        if (_packetId == (int)ClientPackets.welcomeReceived)
                        {
                            Console.WriteLine("Nhận welcomeReceived");
                            int _clientIdCheck = _packet.ReadInt();
                            string _username = _packet.ReadString();

                            ThreadManager.EnqueueAction(() =>
                            {
                                ServerHandle.WelcomeReceived(id, _clientIdCheck, _username);
                            });
                        }
                        else if (_packetId == (int)ClientPackets.playerMovement)
                        {
                            Console.WriteLine("Nhận playermovement");
                            float _x = _packet.ReadFloat();
                            float _y = _packet.ReadFloat();

                            ThreadManager.EnqueueAction(() =>
                            {
                                ServerHandle.PlayerMovement(id, _x, _y);
                            });
                        }
                        else if (_packetId == (int)ClientPackets.ping)   // [HEARTBEAT]
                        {
                            Console.WriteLine("Nhận ping");
                            ThreadManager.EnqueueAction(() => ServerSend.Pong(id));
                        }
                    }

                    stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
                }
                catch (Exception _ex)
                {
                    if (socket != null)
                    {
                        Console.WriteLine($"Loi nhan du lieu tu ID {id}: {_ex}");
                    }
                    ThreadManager.EnqueueAction(() => Server.clients[id].Disconnect());
                }
            }
        }

        public class UDP
        {
            public UdpClient socket = null!;
            private readonly int id;
            private IPEndPoint remoteEndPoint = null!;
            private byte[] receiveBuffer = null!;
            private bool isConnected = false;

            public UDP(int _id)
            {
                id = _id;
            }

            public void Connect(IPEndPoint _remoteEndPoint)
            {
                remoteEndPoint = _remoteEndPoint;
                receiveBuffer = new byte[Constants.DATA_BUFFER_SIZE];
                socket = Server.GetUdpSocket();

                Console.WriteLine($"[UDP] Client {id} UDP endpoint discovered: {remoteEndPoint}");

                isConnected = true;
            }

            // [HEARTBEAT] KHÔNG đóng socket vì nó dùng chung cho mọi client
            public void Disconnect()
            {
                isConnected = false;
                remoteEndPoint = null!;
            }

            public void BeginReceiveFrom()
            {
                if (!isConnected || socket == null)
                    return;

                try
                {
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    socket.Client.BeginReceiveFrom(
                        receiveBuffer, 0, Constants.DATA_BUFFER_SIZE,
                        SocketFlags.None, ref ep, UDPReceiveCallback, null
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] BeginReceiveFrom failed for UDP {id}: {ex.Message}");
                }
            }

            private void UDPReceiveCallback(IAsyncResult _result)
            {
                try
                {
                    if (!isConnected || socket == null)
                        return;

                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    int _byteLength = socket.Client.EndReceiveFrom(_result, ref ep);

                    if (_byteLength <= 0)
                    {
                        BeginReceiveFrom();
                        return;
                    }

                    // [HEARTBEAT] chỉ Touch nếu gói thật sự đến từ endpoint của client này
                    IPEndPoint? _remote = remoteEndPoint;
                    if (_remote != null && ep.Equals(_remote))
                    {
                        Server.Touch(id);
                    }

                    byte[] _data = new byte[_byteLength];
                    Array.Copy(receiveBuffer, _data, _byteLength);

                    using (Packet _packet = new Packet(_data))
                    {
                        _packet.DecryptPayload();

                        int _packetLength = _packet.ReadInt();
                        int _packetId = _packet.ReadInt();

                        ThreadManager.EnqueueAction(() =>
                        {
                            ServerHandle.HandleUDPPacket(id, _packetId, _packet);
                        });
                    }

                    BeginReceiveFrom();
                }
                catch (Exception _ex)
                {
                    Console.WriteLine($"[ERROR] UDP receive error for Client {id}: {_ex.Message}");
                    BeginReceiveFrom();
                }
            }

            public void SendData(Packet _packet)
            {
                if (!isConnected || socket == null || remoteEndPoint == null)
                {
                    Console.WriteLine($"[WARNING] UDP not connected for Client {id}");
                    return;
                }

                try
                {
                    byte[] data = _packet.ToArray();
                    socket.Client.BeginSendTo(data, 0, data.Length, SocketFlags.None, remoteEndPoint, UDPSendCallback, null);
                }
                catch (Exception _ex)
                {
                    Console.WriteLine($"[ERROR] UDP send error for Client {id}: {_ex.Message}");
                }
            }

            private void UDPSendCallback(IAsyncResult _result)
            {
                try
                {
                    if (socket != null)
                    {
                        int bytesSent = socket.Client.EndSendTo(_result);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] UDP send callback error for Client {id}: {ex.Message}");
                }
            }

            public bool IsConnected()
            {
                return isConnected;
            }

            public IPEndPoint GetEndPoint()
            {
                return remoteEndPoint;
            }
        }
    }
}