using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;

namespace YCL.Services
{
    /// <summary>
    /// 陶瓦联机（Terracotta）房间服务。
    ///
    /// 基于 TCP 端口转发的真实联机实现：
    /// - 房主 CreateRoom 时启动一个 TCP 监听器（监听一个自动分配的本地空闲端口），
    ///   把所有入站 TCP 连接转发到 Minecraft 游戏端口（默认 25565）。
    /// - 联机码（GUID 前 8 位）映射到房主地址（局域网 IP:监听端口）。
    /// - 加入方通过联机码用 GetRoomByCode 查询房主地址，让 Minecraft 客户端直接连过去。
    /// - 房主 RemoveRoom 时关闭 TCP 监听器。
    /// </summary>
    public class TerracottaService
    {
        /// <summary>本地维护的房间列表（线程安全起见加锁访问）</summary>
        private readonly List<TerracottaRoom> _rooms = new();

        /// <summary>访问房间列表时用的锁</summary>
        private readonly object _lock = new();

        /// <summary>联机码 → 转发器 的映射，用于房主退出时关闭监听器</summary>
        private readonly Dictionary<string, TcpForwarder> _forwarders = new();

        /// <summary>
        /// 创建一个新房间，并生成一个 8 位联机码（GUID 前 8 位）。
        /// 创建者会自动算作房间内的第一个玩家。
        /// 内部会启动一个 TCP 转发器：监听一个空闲端口，把流量转发到 Minecraft 游戏端口。
        /// </summary>
        /// <param name="name">房间名称</param>
        /// <param name="maxPlayers">最大人数</param>
        /// <param name="port">Minecraft 游戏端口（房主本地 MC 服务器监听的端口，默认 25565）</param>
        /// <returns>新创建的房间信息（含 HostAddress 供客户端连接）</returns>
        public TerracottaRoom CreateRoom(string name, int maxPlayers, int port)
        {
            // 1. 修正参数
            var gamePort = port <= 0 ? 25565 : port;
            var actualMaxPlayers = maxPlayers <= 0 ? 4 : maxPlayers;
            var roomName = string.IsNullOrWhiteSpace(name) ? "未命名房间" : name;

            // 2. 获取本机局域网 IP（如 192.168.x.x）
            var hostIp = GetLocalIpAddress() ?? "127.0.0.1";

            // 3. 启动 TCP 转发器：监听一个空闲端口，转发到游戏端口
            var forwarder = new TcpForwarder();
            int listenPort;
            try
            {
                listenPort = forwarder.Start(gamePort);
            }
            catch (Exception ex)
            {
                // 启动转发器失败时回退到直接使用游戏端口（房主 Minecraft 服务器直接对外）
                Logger.Error("[Terracotta] 启动 TCP 转发器失败，回退到直接使用游戏端口", ex);
                forwarder.Dispose();
                forwarder = null!;
                listenPort = gamePort;
            }

            // 4. 生成房间信息和联机码
            var room = new TerracottaRoom
            {
                RoomCode = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant(),
                RoomName = roomName,
                MaxPlayers = actualMaxPlayers,
                Port = listenPort,
                CurrentPlayers = 1,
                CreatedAt = DateTime.Now,
                HostAddress = $"{hostIp}:{listenPort}"
            };

            lock (_lock)
            {
                _rooms.Add(room);
                if (forwarder != null)
                {
                    _forwarders[room.RoomCode] = forwarder;
                }
            }

            Logger.Info($"[Terracotta] 创建房间：{room.RoomName}，联机码：{room.RoomCode}，房主地址：{room.HostAddress}（转发到游戏端口 {gamePort}）");
            return room;
        }

        /// <summary>
        /// 根据联机码加入一个已存在的房间。
        /// 加入后可通过 GetRoomByCode 拿到房主地址，让 Minecraft 客户端直连。
        /// </summary>
        /// <param name="roomCode">8 位联机码</param>
        /// <returns>成功加入返回 true；房间不存在或已满返回 false</returns>
        public bool JoinRoom(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode)) return false;

            lock (_lock)
            {
                var room = _rooms.FirstOrDefault(r =>
                    string.Equals(r.RoomCode, roomCode.Trim(), StringComparison.OrdinalIgnoreCase));
                if (room == null)
                {
                    Logger.Warn($"[Terracotta] 加入房间失败：找不到联机码 {roomCode}");
                    return false;
                }

                if (room.CurrentPlayers >= room.MaxPlayers)
                {
                    Logger.Warn($"[Terracotta] 加入房间失败：房间 {room.RoomName} 已满");
                    return false;
                }

                room.CurrentPlayers++;
                Logger.Info($"[Terracotta] 加入房间：{room.RoomName}，当前 {room.CurrentPlayers}/{room.MaxPlayers}，房主地址：{room.HostAddress}");
                return true;
            }
        }

        /// <summary>
        /// 获取当前所有房间列表的副本（修改副本不会影响内部状态）。
        /// </summary>
        public List<TerracottaRoom> GetRooms()
        {
            lock (_lock)
            {
                return _rooms.ToList();
            }
        }

        /// <summary>移除指定联机码的房间（房主退出时调用），并关闭对应的 TCP 监听器</summary>
        public bool RemoveRoom(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode)) return false;

            TcpForwarder? forwarderToStop = null;

            lock (_lock)
            {
                var room = _rooms.FirstOrDefault(r =>
                    string.Equals(r.RoomCode, roomCode.Trim(), StringComparison.OrdinalIgnoreCase));
                if (room == null) return false;

                _rooms.Remove(room);

                if (_forwarders.TryGetValue(room.RoomCode, out var f))
                {
                    forwarderToStop = f;
                    _forwarders.Remove(room.RoomCode);
                }

                Logger.Info($"[Terracotta] 房间已关闭：{room.RoomName}（{room.RoomCode}）");
            }

            // 在锁外关闭监听器，避免阻塞其他线程
            if (forwarderToStop != null)
            {
                try
                {
                    forwarderToStop.Stop();
                }
                catch (Exception ex)
                {
                    Logger.Error("[Terracotta] 关闭 TCP 转发器失败", ex);
                }
                finally
                {
                    forwarderToStop.Dispose();
                }
            }

            return true;
        }

        /// <summary>
        /// 根据联机码查询房间信息（含房主地址 HostAddress）。
        /// 客户端加入时调用此方法拿到房主 IP:Port，让 Minecraft 直接连接该地址。
        /// 找不到返回 null。
        /// </summary>
        /// <param name="roomCode">8 位联机码</param>
        public TerracottaRoom? GetRoomByCode(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode)) return null;

            lock (_lock)
            {
                return _rooms.FirstOrDefault(r =>
                    string.Equals(r.RoomCode, roomCode.Trim(), StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// 获取本机局域网 IPv4 地址（如 192.168.x.x）。
        /// 优先返回非回环、非自动私有（169.254.x.x）的 IPv4 地址。
        /// 找不到返回 null。
        /// </summary>
        private static string? GetLocalIpAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                // 第一轮：优先选局域网常见地址（非 127.x、非 169.254.x）
                foreach (var addr in host.AddressList)
                {
                    if (addr.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.ToString();
                    if (!ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                    {
                        return ip;
                    }
                }
                // 第二轮：退而求其次，返回任意 IPv4 地址
                foreach (var addr in host.AddressList)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return addr.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[Terracotta] 获取本机 IP 失败", ex);
            }
            return null;
        }
    }

    /// <summary>
    /// TCP 端口转发器：监听一个本地端口，把所有入站 TCP 连接转发到目标端口（本机的 Minecraft 服务器端口）。
    /// 用于房主把外部联机流量转发到 Minecraft 服务器。
    /// 工作流程：
    ///   1. Start(targetPort) 时让系统自动分配一个空闲监听端口（端口 0）。
    ///   2. 后台线程循环 AcceptTcpClient 接受连接。
    ///   3. 每个连接交给后台任务，双向拷贝字节流（客户端 ↔ 服务器）。
    ///   4. Stop() 关闭监听器，所有正在进行的转发连接会自然断开。
    /// </summary>
    internal sealed class TcpForwarder : IDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Thread? _acceptThread;
        private int _listenPort;
        private int _targetPort;

        /// <summary>
        /// 启动转发器：让系统自动分配一个空闲端口监听，转发到 targetPort。
        /// 返回实际监听的端口号。
        /// </summary>
        public int Start(int targetPort)
        {
            _targetPort = targetPort;
            _cts = new CancellationTokenSource();

            // 端口传 0 让系统自动分配一个空闲端口，避免和已有服务冲突
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();

            // 获取系统实际分配的端口
            _listenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

            // 启动后台线程循环接受连接
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = $"TerracottaForwarder_{_listenPort}"
            };
            _acceptThread.Start();

            Logger.Info($"[Terracotta] TCP 转发器已启动：监听端口 {_listenPort} → 转发到 {_targetPort}");
            return _listenPort;
        }

        /// <summary>停止转发器，关闭监听器（正在进行的转发会断开）</summary>
        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                Logger.Info($"[Terracotta] TCP 转发器已停止：监听端口 {_listenPort}");
            }
            catch (Exception ex)
            {
                Logger.Error("[Terracotta] 停止 TCP 转发器时出错", ex);
            }
        }

        /// <summary>后台线程：循环接受新连接</summary>
        private void AcceptLoop()
        {
            var token = _cts?.Token ?? CancellationToken.None;
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch (SocketException) when (token.IsCancellationRequested)
                {
                    // 正常退出
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("[Terracotta] 接受连接失败", ex);
                    break;
                }

                // 每个连接放到后台任务处理，避免阻塞接受循环
                _ = Task.Run(() => HandleConnectionAsync(client, token));
            }
        }

        /// <summary>处理一个入站连接：建立到目标端口的连接，双向转发字节流</summary>
        private async Task HandleConnectionAsync(TcpClient client, CancellationToken token)
        {
            TcpClient? server = null;
            try
            {
                server = new TcpClient();
                await server.ConnectAsync(IPAddress.Loopback, _targetPort, token);

                var clientStream = client.GetStream();
                var serverStream = server.GetStream();

                // 双向拷贝：客户端 → 服务器，服务器 → 客户端
                var t1 = clientStream.CopyToAsync(serverStream, token);
                var t2 = serverStream.CopyToAsync(clientStream, token);

                // 任意一个方向断开就结束
                await Task.WhenAny(t1, t2);
            }
            catch (OperationCanceledException)
            {
                // 正常关闭，忽略
            }
            catch (Exception ex)
            {
                Logger.Error("[Terracotta] 转发连接出错", ex);
            }
            finally
            {
                try { client.Dispose(); } catch { }
                try { server?.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            _cts?.Dispose();
            _cts = null;
            _listener = null;
        }
    }

    /// <summary>
    /// 陶瓦联机房间信息。
    /// </summary>
    public class TerracottaRoom
    {
        /// <summary>8 位联机码（用于分享给朋友加入）</summary>
        public string RoomCode { get; set; } = string.Empty;

        /// <summary>房间名称</summary>
        public string RoomName { get; set; } = string.Empty;

        /// <summary>最大人数</summary>
        public int MaxPlayers { get; set; }

        /// <summary>本地监听端口（房主转发器监听的端口，客户端连接用）</summary>
        public int Port { get; set; }

        /// <summary>当前人数</summary>
        public int CurrentPlayers { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>房主地址（IP:Port），如 "192.168.1.100:50037"，供客户端 Minecraft 直连用</summary>
        public string HostAddress { get; set; } = string.Empty;

        /// <summary>创建时间的本地显示字符串</summary>
        public string CreatedAtDisplay => CreatedAt.ToString("yyyy-MM-dd HH:mm");

        /// <summary>人数显示字符串（如 "2/4"）</summary>
        public string PlayersDisplay => $"{CurrentPlayers}/{MaxPlayers}";
    }
}
