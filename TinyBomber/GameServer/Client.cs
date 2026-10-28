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

        public class TCP
        {
            public TcpClient socket = null!;
            private readonly int id;
            private NetworkStream stream = null!;
            private byte[] receiveBuffer = null!;

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
                        //disconnect
                        return;
                    }

                    byte[] _data = new byte[_byteLength];
                    Array.Copy(receiveBuffer, _data, _byteLength);

                    using (Packet _packet = new Packet(_data))
                    {
                        _packet.DecryptPayload();

                        int _packetLength = _packet.ReadInt();
                        int _packetId = _packet.ReadInt();

                        if (_packetId == (int)ClientPackets.welcomeReceived)
                        {
                            int _clientIdCheck = _packet.ReadInt();
                            string _username = _packet.ReadString();

                            ThreadManager.EnqueueAction(() =>
                            {
                                ServerHandle.WelcomeReceived(id, _clientIdCheck, _username);
                            });
                        }
                        else if (_packetId == (int)ClientPackets.playerMovement)
                        {
                            float _x = _packet.ReadFloat();
                            float _y = _packet.ReadFloat();

                            ThreadManager.EnqueueAction(() =>
                            {
                                ServerHandle.PlayerMovement(id, _x, _y);
                            });
                        }
                    }

                    stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
                }
                catch (Exception _ex)
                {
                    Console.WriteLine($"Loi nhan du lieu tu ID {id}: {_ex}");
                }
            }
        }
        public class UDP
        {
            public IPEndPoint? endPoint;
            private readonly int id;

            public UDP(int _id)
            {
                id = _id;
            }

            public void Connect(IPEndPoint _endPoint)
            {
                endPoint = _endPoint;
                Console.WriteLine($"[UDP] Client {id} UDP endpoint discovered: {endPoint}");
            }

            public bool IsConnected()
            {
                return endPoint != null;
            }

            public void SendData(Packet _packet)
            {
                if (endPoint != null)
                {
                    Server.SendUDPData(endPoint, _packet);
                }
            }

            public void HandleData(byte[] _packetData)
            {
                try
                {
                    using (Packet _packet = new Packet(_packetData))
                    {
                        _packet.DecryptPayload();

                        int _packetLength = _packet.ReadInt();
                        int _packetId = _packet.ReadInt();

                        if (_packetId == (int)ClientPackets.playerMovement)
                        {
                            // Bóc tách dữ liệu nguyên thủy ngay trên luồng mạng trước khi Packet bị Dispose
                            float _x = _packet.ReadFloat();
                            float _y = _packet.ReadFloat();

                            ThreadManager.EnqueueAction(() =>
                            {
                                ServerHandle.PlayerMovement(id, _x, _y);
                            });
                        }
                    }
                }
                catch (Exception _ex)
                {
                    Console.WriteLine($"[ERROR] Loi giai ma/xu ly UDP tu Client {id}: {_ex.Message}");
                }
            }
        }
    }
}