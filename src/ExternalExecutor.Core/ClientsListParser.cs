using System.Globalization;

namespace ExternalExecutor.Core;

/// <summary>Parses the documented PID-tab-attached newline format.</summary>
public static class ClientsListParser
{
    /// <summary>
    /// Ignores malformed lines and PID zero. For duplicate PIDs the last valid
    /// record wins, while the original display order is retained.
    /// </summary>
    public static IReadOnlyList<RobloxClient> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<RobloxClient>();

        var clients = new List<RobloxClient>();
        var indices = new Dictionary<uint, int>();

        foreach (string line in text.Split('\n'))
        {
            string[] fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 2
                || !uint.TryParse(fields[0].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out uint processId)
                || processId == 0)
                continue;

            string state = fields[1].Trim();
            if (state is not ("0" or "1"))
                continue;

            var client = new RobloxClient(processId, state == "1");
            if (indices.TryGetValue(processId, out int index))
                clients[index] = client;
            else
            {
                indices.Add(processId, clients.Count);
                clients.Add(client);
            }
        }

        return clients.AsReadOnly();
    }
}
