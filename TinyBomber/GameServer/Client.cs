using System;
using System.Net;
using System.Net.Sockets;
using GameShared;

namespace GameServer
{
    class Client
    {
        public int id;
        public Player? player;
        internal GameLogic? Room { get; set; }
        internal string? Username { get; set; }
        internal bool UdpConfirmed { get; set; }
        internal bool IsReady => Username != null && UdpConfirmed;
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
            private TcpFrameReader frameReader = null!;
            private readonly Queue<byte[]> pendingWrites = new();
            private bool writing;
            internal int UdpChallenge { get; } =
                System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue);

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
                frameReader = new TcpFrameReader();

                stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);

                ServerSend.Welcome(this, id, "Chao mung ban den voi may chu Tiny Bomber!");
            }

            public void SendData(Packet packet)
            {
                if (socket == null) return;

                lock (pendingWrites)
                {
                    pendingWrites.Enqueue(packet.ToArray());
                    if (writing) return;
                    writing = true;
                }

                _ = WritePending();
            }

            private async Task WritePending()
            {
                try
                {
                    while (true)
                    {
                        byte[] frame;
                        lock (pendingWrites)
                        {
                            if (!pendingWrites.TryDequeue(out frame!))
                            {
                                writing = false;
                                return;
                            }
                        }

                        await stream.WriteAsync(frame.AsMemory()).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Loi khi gui du lieu toi Player {id}: {ex.Message}");
                    ThreadManager.EnqueueAction(() => Server.Disconnect(id, this));
                }
            }

            private void HandleFrame(byte[] _data)
            {
                using (Packet _packet = new Packet(_data))
                {
                    _packet.DecryptPayload();

                    int _packetLength = _packet.ReadInt();
                    int _packetId = _packet.ReadInt();

                    if (_packetId == (int)ClientPackets.welcomeReceived)
                    {
                        int _clientIdCheck = _packet.ReadInt();
                        string _username = _packet.ReadString();

                        EnqueueCurrent(slot => ServerHandle.WelcomeReceived(slot, _clientIdCheck, _username));
                    }
                    else if (_packetId == (int)ClientPackets.placeBomb)
                    {
                        int roundId = _packet.ReadInt();
                        EnqueueCurrent(slot => slot.Room?.PlaceBomb(slot, roundId));
                    }
                    else if (_packetId == (int)ClientPackets.playerMovement)
                    {
                        int roundId = _packet.ReadInt();
                        float _x = _packet.ReadFloat();
                        float _y = _packet.ReadFloat();
                        EnqueueCurrent(slot => ServerHandle.PlayerMovement(slot, roundId, _x, _y));
                    }
                    else if (_packetId == (int)ClientPackets.udpReady)
                    {
                        int challenge = _packet.ReadInt();
                        EnqueueCurrent(slot => ServerHandle.ConfirmUdp(slot, challenge));
                    }
                    else if (_packetId == (int)ClientPackets.requestStart)
                    {
                        int roundId = _packet.ReadInt();
                        EnqueueCurrent(slot => slot.Room?.StartRound(slot, roundId));
                    }
                }
            }

            private void EnqueueCurrent(Action<Client> action)
            {
                ThreadManager.EnqueueAction(() =>
                {
                    Client slot = Server.clients[id];
                    if (slot.tcp == this) action(slot);
                });
            }

            private void ReceiveCallback(IAsyncResult _result)
            {
                try
                {
                    int _byteLength = stream.EndRead(_result);
                    if (_byteLength <= 0)
                    {
                        ThreadManager.EnqueueAction(() => Server.Disconnect(id, this));
                        return;
                    }

                    frameReader.Feed(receiveBuffer.AsSpan(0, _byteLength), HandleFrame);

                    stream.BeginRead(receiveBuffer, 0, Constants.DATA_BUFFER_SIZE, ReceiveCallback, null);
                }
                catch (Exception _ex)
                {
                    Console.WriteLine($"Loi nhan du lieu tu ID {id}: {_ex}");
                    ThreadManager.EnqueueAction(() => Server.Disconnect(id, this));
                }
            }
        }
        public class UDP
        {
            public volatile IPEndPoint? endPoint;
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

            public void SendData(Packet _packet)
            {
                var recipient = endPoint;
                if (recipient != null) Server.SendUDPData(recipient, _packet);
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
                            int roundId = _packet.ReadInt();
                            float _x = _packet.ReadFloat();
                            float _y = _packet.ReadFloat();

                            ThreadManager.EnqueueAction(() =>
                            {
                                Client slot = Server.clients[id];
                                if (slot.udp != this) return;
                                ServerHandle.PlayerMovement(slot, roundId, _x, _y);
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
