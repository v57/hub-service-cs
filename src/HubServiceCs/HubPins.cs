using System.Text.Encodings.Web;
using System.Text.Json;

namespace HubServiceCs;

// HubPins holds pinned hub keys (Docs/Devices/protocol.md section 2): the
// file `hubs.json` beside the service key file, a JSON object
// {"<address>": "<hub SPKI>"} keyed by the normalized address
// (Service.NormalizeAddress). A service learns a hub's key from `hub/info` on
// its first (v0) connection and signs v1 tokens bound to it from then on.
public sealed class HubPins
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();

    public HubPins(string path)
    {
        Path = path;
    }

    public string Path { get; }

    // Beside returns the pins stored in `hubs.json` beside keyPath.
    public static HubPins Beside(string keyPath)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(keyPath))!;
        return new HubPins(System.IO.Path.Combine(directory, "hubs.json"));
    }

    // Get returns the pinned key for address, or null.
    public string? Get(string address)
    {
        lock (_gate)
        {
            return Read().TryGetValue(address, out var key) ? key : null;
        }
    }

    // Put pins hubKey for address.
    public void Put(string address, string hubKey)
    {
        lock (_gate)
        {
            var pins = Read();
            pins[address] = hubKey;
            Write(pins);
        }
    }

    // Remove forgets the pinned key for address.
    public void Remove(string address)
    {
        lock (_gate)
        {
            var pins = Read();
            if (pins.Remove(address))
            {
                Write(pins);
            }
        }
    }

    private Dictionary<string, string> Read()
    {
        var pins = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(Path))
            {
                return pins;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(Path));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    pins[property.Name] = property.Value.GetString()!;
                }
            }
        }
        catch
        {
            // A corrupt pin file counts as empty: the next hub/info pins again.
        }

        return pins;
    }

    private void Write(Dictionary<string, string> pins)
    {
        KeyFile.WriteAtomic(Path, JsonSerializer.Serialize(pins, WriteOptions), secure: false);
    }
}
