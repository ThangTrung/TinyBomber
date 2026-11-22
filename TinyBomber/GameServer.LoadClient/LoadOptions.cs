using System.Globalization;

namespace GameServer.LoadClient;

internal enum WorkloadMode { LobbyIdle, GameplayIdle, GameplayActive }

internal sealed record LoadOptions(
    string Server = "127.0.0.1",
    int Clients = 8,
    WorkloadMode Mode = WorkloadMode.LobbyIdle,
    int MovementHz = 20,
    int Seconds = 10,
    int WarmupSeconds = 0)
{
    internal const int MaxMovementHz = 50; // Unity's 0.02 s fixed timestep.
    internal const int MaxSeconds = 600; // Keeps unique movement targets inside one spawn cell.

    internal const string Usage = """
        GameServer.LoadClient [--server <IPv4 hoặc hostname>] [--clients <1-2048>]
           [--mode lobby-idle|gameplay-idle|gameplay-active] [--movement-hz <1-50>] [--seconds <1-600>]
           [--warmup-seconds <0-599>]
        Mặc định: 127.0.0.1, 8 client, lobby-idle, 20 Hz, đo 10 giây, warm-up 0 giây.
        Tổng thời gian warm-up và đo tối đa 600 giây.
        Chế độ gameplay cần ít nhất 2 client. Phòng được chia đều, tối đa 4 client/phòng.
        """;

    internal static LoadOptions Parse(string[] args)
    {
        var options = new LoadOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            string option = args[i];
            if (i + 1 == args.Length || !seen.Add(option))
                throw new ArgumentException($"Thiếu giá trị hoặc lặp đối số {option}.");
            string value = args[i + 1];
            options = option switch
            {
                "--server" when !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--") =>
                    options with { Server = value },
                "--clients" => options with { Clients = ParseInt(option, value, 2048) },
                "--movement-hz" => options with { MovementHz = ParseInt(option, value, MaxMovementHz) },
                "--seconds" => options with { Seconds = ParseInt(option, value, MaxSeconds) },
                "--warmup-seconds" => options with { WarmupSeconds = ParseInt(option, value, MaxSeconds - 1, minimum: 0) },
                "--mode" => options with
                {
                    Mode = value switch
                    {
                        "lobby-idle" => WorkloadMode.LobbyIdle,
                        "gameplay-idle" => WorkloadMode.GameplayIdle,
                        "gameplay-active" => WorkloadMode.GameplayActive,
                        _ => throw new ArgumentException($"Workload không hợp lệ: {value}.")
                    }
                },
                _ => throw new ArgumentException($"Đối số hoặc giá trị không hợp lệ: {option}.")
            };
        }
        if (options.Mode != WorkloadMode.LobbyIdle && options.Clients < 2)
            throw new ArgumentException("Chế độ gameplay cần ít nhất 2 client.");
        if (options.WarmupSeconds + options.Seconds > MaxSeconds)
            throw new ArgumentException($"Tổng thời gian warm-up và đo tối đa {MaxSeconds} giây.");
        return options;
    }

    private static int ParseInt(string option, string value, int maximum, int minimum = 1)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ||
            parsed < minimum || parsed > maximum)
            throw new ArgumentException($"{option} cần số nguyên từ {minimum} đến {maximum}.");
        return parsed;
    }
}
