using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Stratus.Core.Services;

/// <summary>One resolver measured against a live DNS query.</summary>
public sealed record DnsBenchmarkResult(string Label, string Server, double AverageMs, int Samples, bool Ok, string? Error)
{
    public string LatencyText => Ok ? $"{AverageMs:F0} ms" : "no response";
}

/// <summary>
/// Measures real resolver latency by sending small UDP DNS queries for a fixed name
/// straight to each public resolver. No PowerShell, no system cache, no third party.
/// </summary>
public static class DnsBenchmarkService
{
    private const int TimeoutMs = 2000;
    private const string QueryName = "example.com";

    public static IReadOnlyList<DnsBenchmarkResult> Run(int samplesPerServer = 3)
    {
        var results = new List<DnsBenchmarkResult>();

        foreach (var profile in DnsService.Profiles)
        {
            // IPv6-only rows would double the table without telling the user anything the IPv4
            // row does not already show.
            if (profile.Primary is null || !profile.BenchmarkEligible)
                continue;

            results.Add(Measure(profile.Label, profile.Primary, samplesPerServer));

            // Secondaries are real resolvers too: measuring only the primary hid a dead fallback.
            if (profile.Secondary is not null)
            {
                var name = profile.Label.Split('(')[0].Trim();
                results.Add(Measure($"{name} secondary ({profile.Secondary})", profile.Secondary, samplesPerServer));
            }
        }

        return results.OrderBy(r => r.Ok ? 0 : 1).ThenBy(r => r.AverageMs).ToList();
    }

    /// <summary>Times one server over a few queries and folds the samples into a result row.</summary>
    private static DnsBenchmarkResult Measure(string label, string server, int samplesPerServer)
    {
        var times = new List<double>();
        string? error = null;

        for (var i = 0; i < samplesPerServer; i++)
        {
            var (ok, ms, message) = Query(server);
            if (ok)
                times.Add(ms);
            else
                error = message;
        }

        return times.Count > 0
            ? new DnsBenchmarkResult(label, server, times.Average(), times.Count, true, null)
            : new DnsBenchmarkResult(label, server, 0, 0, false, error ?? "no response");
    }

    /// <summary>One DNS query round trip. Returns elapsed milliseconds on success.</summary>
    private static (bool Ok, double Ms, string? Error) Query(string server)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = TimeoutMs;
            udp.Connect(server, 53);

            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var request = BuildQuery(id, QueryName);

            var watch = Stopwatch.StartNew();
            udp.Send(request, request.Length);
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            var response = udp.Receive(ref endpoint);
            watch.Stop();

            if (response.Length < 12 || (response[0] << 8 | response[1]) != id)
                return (false, 0, "malformed response");

            var rcode = response[3] & 0x0F;
            if (rcode != 0)
                return (false, 0, $"resolver error {rcode}");

            return (true, watch.Elapsed.TotalMilliseconds, null);
        }
        catch (SocketException ex)
        {
            return (false, 0, ex.SocketErrorCode == SocketError.TimedOut ? "timed out" : ex.Message);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private static byte[] BuildQuery(ushort id, string name)
    {
        var bytes = new List<byte>(32)
        {
            (byte)(id >> 8), (byte)(id & 0xFF),
            0x01, 0x00, // standard query, recursion desired
            0x00, 0x01, // one question
            0x00, 0x00, // no answers
            0x00, 0x00, // no authority
            0x00, 0x00, // no additional
        };

        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0x00);          // end of name
        bytes.Add(0x00); bytes.Add(0x01); // QTYPE A
        bytes.Add(0x00); bytes.Add(0x01); // QCLASS IN

        return bytes.ToArray();
    }
}
