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