using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AdamsToolkit.Core;

public record AdapterInfo(string Name, string Description, bool IsWifi, long SpeedMbps, string Ipv4, string Gateway, IReadOnlyList<string> Dns);

public record DnsBench(string Name, string Server, double MedianMs, int Ok, int Total)
{
    public string Summary => Ok == 0 ? "sem resposta" : $"{MedianMs:0} ms";
}

/// <summary>
/// Ferramentas de rede SÓ DE LEITURA/MEDIÇÃO.
///
/// De propósito não há "limpar cache DNS", "limpar ARP", "repor pilha de rede" nem
/// "mudar DNS": todos esvaziam a cache DNS do Windows, que é uma das coisas que um
/// PC check consulta. O benchmark de DNS fala com os servidores por UDP diretamente,
/// sem passar pelo resolver do Windows — não acrescenta nem tira nada à cache.
/// </summary>
public static class NetTools
{
    // ---------- adaptadores ----------

    public static List<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var ip = n.GetIPProperties();
                var gw = ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (gw == null) continue; // sem gateway = virtual (Hyper-V, VPN parada…)
                var v4 = ip.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                list.Add(new AdapterInfo(
                    n.Name, n.Description,
                    n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    n.Speed > 0 ? n.Speed / 1_000_000 : 0,
                    v4?.ToString() ?? "—", gw.ToString(),
                    ip.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList()));
            }
        }
        catch (Exception ex) { Log.Swallow("listar adaptadores", ex); }
        return list;
    }

    // ---------- ping ----------

    public static async Task<PingResult> PingAsync(string host, int count = 8)
    {
        var times = new List<long>();
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            try
            {
                var r = await ping.SendPingAsync(host, 1000);
                if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime);
            }
            catch { }
            await Task.Delay(120);
        }
        if (times.Count == 0) return new PingResult(count, 0, 0, 0, 0, 0);
        double jitter = 0;
        for (var i = 1; i < times.Count; i++) jitter += Math.Abs(times[i] - times[i - 1]);
        jitter = times.Count > 1 ? jitter / (times.Count - 1) : 0;
        return new PingResult(count, times.Count, times.Min(), times.Average(), times.Max(), jitter);
    }

    // ---------- benchmark de DNS (UDP direto, sem cache do Windows) ----------

    public static readonly (string name, string ip)[] DnsServers =
    {
        ("Cloudflare", "1.1.1.1"), ("Google", "8.8.8.8"), ("Quad9", "9.9.9.9"), ("OpenDNS", "208.67.222.222"),
    };

    private static readonly string[] BenchDomains = { "discord.com", "cfx.re", "google.com", "cloudflare.com", "youtube.com" };

    public static async Task<List<DnsBench>> BenchDnsAsync(IEnumerable<(string name, string ip)> servers)
    {
        var tasks = servers.Select(async s =>
        {
            var times = new List<double>();
            foreach (var d in BenchDomains)
            {
                var t = await QueryMsAsync(s.ip, d);
                if (t >= 0) times.Add(t);
            }
            times.Sort();
            var median = times.Count == 0 ? 0 : times[times.Count / 2];
            return new DnsBench(s.name, s.ip, median, times.Count, BenchDomains.Length);
        });
        return (await Task.WhenAll(tasks)).OrderBy(b => b.Ok == 0).ThenBy(b => b.MedianMs).ToList();
    }

    private static async Task<double> QueryMsAsync(string server, string domain)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(IPAddress.Parse(server), 53);
            var q = BuildQuery(domain);
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(q, q.Length);
            var recv = udp.ReceiveAsync();
            if (await Task.WhenAny(recv, Task.Delay(1500)) != recv) return -1;
            var r = recv.Result.Buffer;
            // mesma transação e flag de resposta (QR) ligada
            return r.Length > 3 && r[0] == q[0] && r[1] == q[1] && (r[2] & 0x80) != 0 ? sw.Elapsed.TotalMilliseconds : -1;
        }
        catch { return -1; }
    }

    private static byte[] BuildQuery(string domain)
    {
        var id = (ushort)Random.Shared.Next(1, 65535);
        var ms = new MemoryStream();
        ms.Write(new byte[] { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }); // RD=1, 1 pergunta
        foreach (var label in domain.Split('.'))
        {
            ms.WriteByte((byte)label.Length);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }
        ms.Write(new byte[] { 0, 0, 1, 0, 1 }); // fim do nome, tipo A, classe IN
        return ms.ToArray();
    }

    // ---------- teste de velocidade (Cloudflare) ----------

    public record SpeedResult(double DownMbps, double UpMbps, double PingMs, double JitterMs);

    private const string CfBase = "https://speed.cloudflare.com";

    /// <summary>
    /// Ping HTTP + download (4 ligações, ~8 s) + upload (3 ligações, ~6 s).
    /// <paramref name="progress"/> recebe (fase, valor atual em Mbps ou ms, 0..1).
    /// </summary>
    public static async Task<SpeedResult> SpeedTestAsync(IProgress<(string phase, double value, double frac)> progress, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 8, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AdamsToolkit/2");

        // --- latência: 1º pedido abre a ligação (TLS), não conta ---
        var lat = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            using (var r = await http.GetAsync($"{CfBase}/__down?bytes=0", ct)) { }
            if (i > 0) lat.Add(sw.Elapsed.TotalMilliseconds);
            progress.Report(("ping", lat.Count > 0 ? lat.Min() : 0, (i + 1) / 9.0));
        }
        lat.Sort();
        var ping = lat[lat.Count / 2];
        double jit = 0;
        for (var i = 1; i < lat.Count; i++) jit += Math.Abs(lat[i] - lat[i - 1]);
        jit /= Math.Max(1, lat.Count - 1);

        var down = await MeasureAsync("download", 4, TimeSpan.FromSeconds(8), async (counter, token) =>
        {
            using var r = await http.GetAsync($"{CfBase}/__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, token);
            await using var s = await r.Content.ReadAsStreamAsync(token);
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await s.ReadAsync(buf, token)) > 0) Interlocked.Add(ref counter.Value, n);
        }, progress, ct);

        var up = await MeasureAsync("upload", 3, TimeSpan.FromSeconds(6), async (counter, token) =>
        {
            using var content = new CountingContent(8 * 1024 * 1024, counter);
            using var r = await http.PostAsync($"{CfBase}/__up", content, token);
        }, progress, ct);

        return new SpeedResult(down, up, ping, jit);
    }

    public class Counter { public long Value; }

    /// <summary>N workers em ciclo durante <paramref name="duration"/>; ignora o 1º segundo (arranque TCP).</summary>
    private static async Task<double> MeasureAsync(string phase, int workers, TimeSpan duration,
        Func<Counter, CancellationToken, Task> job, IProgress<(string, double, double)> progress, CancellationToken ct)
    {
        var counter = new Counter();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await job(counter, stop.Token); }
                catch when (stop.IsCancellationRequested) { return; }
                catch { await Task.Delay(200); }
            }
        })).ToList();

        var total = Stopwatch.StartNew();
        long baseBytes = 0;
        var baseTime = TimeSpan.Zero;
        double mbps = 0;
        var whole = duration + TimeSpan.FromSeconds(1);
        while (total.Elapsed < whole)
        {
            await Task.Delay(200, ct);
            if (baseTime == TimeSpan.Zero && total.Elapsed >= TimeSpan.FromSeconds(1))
            {
                baseBytes = Interlocked.Read(ref counter.Value);
                baseTime = total.Elapsed;
            }
            else if (baseTime != TimeSpan.Zero)
            {
                var secs = (total.Elapsed - baseTime).TotalSeconds;
                mbps = (Interlocked.Read(ref counter.Value) - baseBytes) * 8 / secs / 1_000_000;
            }
            // a barra avança desde o 1º instante (o arranque TCP também conta para o tempo)
            progress.Report((phase, mbps, Math.Min(1, total.Elapsed.TotalSeconds / whole.TotalSeconds)));
        }
        stop.Cancel();
        try { await Task.WhenAll(tasks); } catch { }
        return mbps;
    }

    /// <summary>Corpo de upload que conta os bytes à medida que saem (não só no fim).</summary>
    private sealed class CountingContent : HttpContent
    {
        private static readonly byte[] Chunk = MakeChunk();
        private readonly long _size;
        private readonly Counter _counter;

        public CountingContent(long size, Counter counter)
        {
            _size = size;
            _counter = counter;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        private static byte[] MakeChunk()
        {
            var b = new byte[64 * 1024];
            Random.Shared.NextBytes(b); // aleatório: nada de compressão a inflacionar o resultado
            return b;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            long sent = 0;
            while (sent < _size)
            {
                var n = (int)Math.Min(Chunk.Length, _size - sent);
                await stream.WriteAsync(Chunk.AsMemory(0, n));
                sent += n;
                Interlocked.Add(ref _counter.Value, n);
            }
        }

        protected override bool TryComputeLength(out long length) { length = _size; return true; }
    }
}
