using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace RelayServer
{
    class Program
    {
        private static List<ClientInfo> clients = new List<ClientInfo>();
        private static TcpListener server;

        static async Task Main(string[] args)
        {
            // Railway сам задаёт порт
            int port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "8080");
            
            server = new TcpListener(IPAddress.Any, port);
            server.Start();
            
            Console.WriteLine($"✅ Сервер запущен на порту {port}");
            
            // Получаем публичный URL
            string railwayUrl = Environment.GetEnvironmentVariable("RAILWAY_PUBLIC_DOMAIN");
            if (!string.IsNullOrEmpty(railwayUrl))
            {
                Console.WriteLine($"🌐 Адрес для подключения: {railwayUrl}");
            }
            else
            {
                Console.WriteLine($"🌐 Адрес: localhost:{port}");
            }
            
            while (true)
            {
                var client = await server.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClient(client));
            }
        }

        private static async Task HandleClient(TcpClient tcpClient)
        {
            var clientInfo = new ClientInfo { TcpClient = tcpClient, Stream = tcpClient.GetStream() };
            
            try
            {
                byte[] buffer = new byte[4096];
                int bytesRead = await clientInfo.Stream.ReadAsync(buffer, 0, buffer.Length);
                string username = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                clientInfo.Username = username;
                
                lock (clients) { clients.Add(clientInfo); }
                
                Console.WriteLine($"🟢 {username} подключился | Всего: {clients.Count}");
                
                // Оповещаем всех
                await BroadcastMessage($"✨ {username} присоединился к чату!", clientInfo.Username);
                
                while (true)
                {
                    bytesRead = await clientInfo.Stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;
                    
                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Console.WriteLine($"💬 {username}: {message}");
                    await BroadcastMessage($"{username}: {message}", clientInfo.Username);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка: {ex.Message}");
            }
            finally
            {
                lock (clients) { clients.Remove(clientInfo); }
                Console.WriteLine($"🔴 {clientInfo.Username} отключился | Осталось: {clients.Count}");
                await BroadcastMessage($"❌ {clientInfo.Username} покинул чат", clientInfo.Username);
                clientInfo.Disconnect();
            }
        }

        private static async Task BroadcastMessage(string message, string excludeUser = null)
        {
            lock (clients)
            {
                foreach (var client in clients)
                {
                    if (client.Username != excludeUser)
                    {
                        _ = client.SendMessage(message);
                    }
                }
            }
        }
    }

    class ClientInfo
    {
        public TcpClient TcpClient { get; set; }
        public NetworkStream Stream { get; set; }
        public string Username { get; set; }

        public async Task SendMessage(string message)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                await Stream.WriteAsync(data, 0, data.Length);
            }
            catch { }
        }

        public void Disconnect()
        {
            try { Stream?.Close(); TcpClient?.Close(); } catch { }
        }
    }
}
