using System;
using GameShared;

namespace GameServer
{
    class ServerSend
    {
        private static void SendTCPData(int _toClient, Packet _packet)
        {
            _packet.WriteLength(); // Ghi chèn độ dài gói tin vào đầu byte array
            Server.clients[_toClient].tcp.SendData(_packet);
        }

        public static void Welcome(int _toClient, string _msg)
        {
            // Sử dụng "using" để Packet tự động được dọn dẹp khỏi RAM sau khi gửi xong
            using (Packet _packet = new Packet((int)ServerPackets.welcome))
            {
                _packet.Write(_msg);      // Gửi nội dung tin nhắn
                _packet.Write(_toClient); // Gửi ID của chính người chơi đó

                SendTCPData(_toClient, _packet);
            }
        }
    }
}