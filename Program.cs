using System;
using System.Collections.Generic;
using System.Linq;
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
        private static bool isRunning = true;

        static async Task Main(string[] args)
        {
            // Railway сам задаёт порт через переменную окружения PORT
            int port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "8080");

            Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║     🌐 РЕЛЕЙНЫЙ СЕРВЕР МЕССЕНДЖЕРА ДЛЯ RAILWAY             ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            server = new TcpListener(IPAddress.Any, port);
            server.Start();

            Console.WriteLine($"✅ Сервер запущен на порту {port}");

            // Получаем публичный URL от Railway
            string railwayUrl = Environment.GetEnvironmentVariable("RAILWAY_PUBLIC_DOMAIN");
            if (!string.IsNullOrEmpty(railwayUrl))
            {
                Console.WriteLine($"🌐 Адрес для подключения: {railwayUrl}");
                Console.WriteLine($"📱 СООБЩИТЕ ЭТОТ АДРЕС ДРУЗЬЯМ!");
            }
            else
            {
                Console.WriteLine($"🌐 Локальный адрес: 127.0.0.1:{port}");
            }

            Console.WriteLine($"📊 Статистика: Ожидание подключений...");
            Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            // Запускаем таймер статистики
            _ = Task.Run(ShowStats);

            // Основной цикл принятия клиентов
            while (isRunning)
            {
                try
                {
                    var client = await server.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClient(client));
                }
                catch (Exception ex)
                {
                    if (isRunning)
                        Console.WriteLine($"❌ Ошибка: {ex.Message}");
                }
            }
        }

        private static async Task ShowStats()
        {
            while (isRunning)
            {
                await Task.Delay(30000); // Каждые 30 секунд
                Console.WriteLine($"📊 Статистика: Онлайн: {clients.Count} | Комнат: {clients.Select(c => c.Room).Distinct().Count()}");
            }
        }

        private static async Task HandleClient(TcpClient tcpClient)
        {
            var clientInfo = new ClientInfo { TcpClient = tcpClient, Stream = tcpClient.GetStream() };

            try
            {
                // Получаем имя пользователя и комнату
                byte[] buffer = new byte[4096];
                int bytesRead = await clientInfo.Stream.ReadAsync(buffer, 0, buffer.Length);
                string initData = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var parts = initData.Split('|');

                clientInfo.Username = parts[0];
                clientInfo.Room = parts.Length > 1 ? parts[1] : "general";

                lock (clients) { clients.Add(clientInfo); }

                Console.WriteLine($"🟢 [+] {clientInfo.Username} подключился | Комната: {clientInfo.Room} | Всего: {clients.Count}");

                // Оповещаем всех в комнате
                await BroadcastToRoom(clientInfo.Room, $"✨ {clientInfo.Username} присоединился к чату!", clientInfo.Username);
                await SendUserList(clientInfo.Room);

                // Обработка сообщений
                while (true)
                {
                    bytesRead = await clientInfo.Stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Обработка команд
                    if (message.StartsWith("/room "))
                    {
                        string newRoom = message.Substring(6);
                        await SwitchRoom(clientInfo, newRoom);
                    }
                    else if (message.StartsWith("/private "))
                    {
                        var privateMsg = message.Substring(9);
                        int spaceIndex = privateMsg.IndexOf(' ');
                        if (spaceIndex > 0)
                        {
                            string targetUser = privateMsg.Substring(0, spaceIndex);
                            string privateText = privateMsg.Substring(spaceIndex + 1);
                            await SendPrivateMessage(clientInfo.Username, targetUser, privateText);
                        }
                        else
                        {
                            await clientInfo.SendMessage("❌ Использование: /private [имя] [сообщение]");
                        }
                    }
                    else if (message.StartsWith("/users"))
                    {
                        await SendUserList(clientInfo.Room);
                    }
                    else if (message.StartsWith("/help"))
                    {
                        await clientInfo.SendMessage("📖 Команды: /room [название] | /private [имя] [текст] | /users | /help");
                    }
                    else
                    {
                        Console.WriteLine($"💬 [{clientInfo.Room}] {clientInfo.Username}: {message}");
                        await BroadcastToRoom(clientInfo.Room, $"{clientInfo.Username}: {message}", clientInfo.Username);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка {clientInfo.Username}: {ex.Message}");
            }
            finally
            {
                lock (clients) { clients.Remove(clientInfo); }
                if (!string.IsNullOrEmpty(clientInfo.Username))
                {
                    Console.WriteLine($"🔴 [-] {clientInfo.Username} отключился | Осталось: {clients.Count}");
                    await BroadcastToRoom(clientInfo.Room, $"❌ {clientInfo.Username} покинул чат", clientInfo.Username);
                    await SendUserList(clientInfo.Room);
                }
                clientInfo.Disconnect();
            }
        }

        private static async Task SwitchRoom(ClientInfo client, string newRoom)
        {
            string oldRoom = client.Room;
            await BroadcastToRoom(oldRoom, $"👋 {client.Username} покинул комнату {oldRoom}", client.Username);

            client.Room = newRoom;
            await client.SendMessage($"🏠 Вы перешли в комнату '{newRoom}'");
            await BroadcastToRoom(newRoom, $"🟢 {client.Username} присоединился к комнате {newRoom}", client.Username);
            await SendUserList(newRoom);

            Console.WriteLine($"🔄 {client.Username} перешёл из {oldRoom} в {newRoom}");
        }

        private static async Task SendPrivateMessage(string from, string to, string message)
        {
            lock (clients)
            {
                var target = clients.Find(c => c.Username.Equals(to, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                {
                    _ = target.SendMessage($"🔒 [Лично от {from}]: {message}");
                    var sender = clients.Find(c => c.Username == from);
                    if (sender != null)
                        _ = sender.SendMessage($"🔒 [Лично для {to}]: {message}");
                    Console.WriteLine($"🔒 Приват: {from} → {to}: {message}");
                }
                else
                {
                    var sender = clients.Find(c => c.Username == from);
                    if (sender != null)
                        _ = sender.SendMessage($"❌ Пользователь '{to}' не найден в сети");
                }
            }
        }

        private static async Task SendUserList(string room)
        {
            lock (clients)
            {
                var roomUsers = clients.FindAll(c => c.Room == room);
                string userList = "/users " + string.Join(",", roomUsers.Select(u => u.Username));

                foreach (var client in roomUsers)
                {
                    _ = client.SendMessage(userList);
                }
            }
        }

        private static async Task BroadcastToRoom(string room, string message, string excludeUser = null)
        {
            lock (clients)
            {
                foreach (var client in clients)
                {
                    if (client.Room == room && client.Username != excludeUser)
                    {
                        _ = client.SendMessage(message);
                    }
                }
            }
        }
    }

    public class ClientInfo
    {
        public TcpClient TcpClient { get; set; }
        public NetworkStream Stream { get; set; }
        public string Username { get; set; }
        public string Room { get; set; } = "general";

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
            try
            {
                Stream?.Close();
                TcpClient?.Close();
            }
            catch { }
        }
    }
}