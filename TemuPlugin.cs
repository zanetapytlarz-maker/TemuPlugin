using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

public class TemuPlugin
{
    private static readonly Lazy<TemuApiSettings> Settings = new(TemuApiSettings.Load);
    private static readonly Lazy<TemuApiClient> ApiClient = new(() => new TemuApiClient(Settings.Value));

    [System.ComponentModel.DisplayName("Panel TEMU PL")]
    public static void T_Panel()
    {
        try
        {
            var settings = Settings.Value;
            MessageBox.Show(
                $"Połączenie z TEMU skonfigurowane dla sklepu: {settings.ShopId}.\nAdres API: {settings.BaseUrl}",
                "TEMU PL");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Nie udało się wczytać konfiguracji TEMU.\n{ex.Message}",
                "TEMU PL - błąd",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    [System.ComponentModel.DisplayName("Pobierz nowe zamówienia")]
    public static void T_PobierzZamowienia()
    {
        try
        {
            var client = ApiClient.Value;
            var orders = client.GetNewOrdersAsync(CancellationToken.None).GetAwaiter().GetResult();
            var filePath = SaveOrdersToFile(orders);

            var message = orders.Count == 0
                ? "Brak nowych zamówień do pobrania."
                : $"Pobrano {orders.Count} nowych zamówień z TEMU.\nZapisano do pliku: {filePath}";

            MessageBox.Show(
                message,
                "TEMU PL");
        }
        catch (Exception ex)
        {
            LogError("pobieranie zamówień", ex);
            MessageBox.Show(
                $"Wystąpił problem podczas pobierania zamówień.\n{ex.Message}",
                "TEMU PL - błąd",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    [System.ComponentModel.DisplayName("Pakuj (TEMU)")]
    public static void T_Pakuj(string xml)
    {
        try
        {
            var orderId = ExtractOrderId(xml);
            if (string.IsNullOrWhiteSpace(orderId))
            {
                MessageBox.Show(
                    "Nie znaleziono identyfikatora zamówienia w przekazanym XML.",
                    "TEMU PL - błąd",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var client = ApiClient.Value;
            var labelResult = client.CreateLabelAsync(orderId, CancellationToken.None).GetAwaiter().GetResult();
            var savedPath = SaveLabelFile(labelResult);

            MessageBox.Show(
                $"Etykieta dla zamówienia {orderId} została zapisana do: {savedPath}",
                "TEMU PL");
        }
        catch (Exception ex)
        {
            LogError("drukowanie etykiety", ex);
            MessageBox.Show(
                $"Wystąpił błąd podczas drukowania etykiety.\n{ex.Message}",
                "TEMU PL - błąd",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string SaveOrdersToFile(IReadOnlyCollection<TemuOrder> orders)
    {
        var ordersDir = EnsureDirectory("Orders");

        var fileName = $"temu-orders-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        var filePath = Path.Combine(ordersDir, fileName);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(orders, options);
        File.WriteAllText(filePath, json, Encoding.UTF8);

        return filePath;
    }

    private static string SaveLabelFile(LabelResult label)
    {
        var labelDir = EnsureDirectory("Labels");

        var extension = label.Format.Equals("PDF", StringComparison.OrdinalIgnoreCase) ? ".pdf" : ".zpl";
        var fileName = string.IsNullOrWhiteSpace(label.FileName)
            ? $"temu-label-{label.OrderId}{extension}"
            : label.FileName;
        var filePath = Path.Combine(labelDir, fileName);

        File.WriteAllBytes(filePath, label.Content);
        return filePath;
    }

    private static string ExtractOrderId(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return string.Empty;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var orderElement = doc.Descendants("order").FirstOrDefault();
            var id = orderElement?.Attribute("id")?.Value ?? orderElement?.Element("id")?.Value;
            return id ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void LogError(string operation, Exception ex)
    {
        try
        {
            var logsDir = EnsureDirectory("Logs");
            var logPath = Path.Combine(logsDir, "temu-plugin.log");
            var message = $"[{DateTime.Now:O}] Błąd podczas {operation}: {ex}\n";
            File.AppendAllText(logPath, message);
        }
        catch
        {
            // Ignorujemy logowanie, gdy zapis do pliku jest niemożliwy.
        }
    }

    private static string EnsureDirectory(string relativeName)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var targetDir = Path.Combine(baseDir, relativeName);
        Directory.CreateDirectory(targetDir);
        return targetDir;
    }
}

public record TemuOrder(string OrderId, string Status, DateTime CreatedAt);

public record LabelResult(string OrderId, byte[] Content, string Format, string FileName);

public sealed class TemuApiSettings
{
    public string BaseUrl { get; init; } = "https://api.partner.temu.com";
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string ShopId { get; init; } = string.Empty;
    public string LabelFormat { get; init; } = "PDF";
    private const string ConfigFileName = "temu.config.json";

    public static TemuApiSettings Load()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var configPath = Path.Combine(baseDir, ConfigFileName);

        if (!File.Exists(configPath))
        {
            var samplePath = Path.Combine(baseDir, "temu.config.sample.json");
            if (!File.Exists(samplePath))
            {
                var sample = new TemuApiSettings
                {
                    ClientId = "Wprowadź_ClientId_z_umowy",
                    ClientSecret = "Wprowadź_ClientSecret_z_umowy",
                    ShopId = "Wprowadź_identyfikator_sklepu"
                };

                var sampleJson = JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(samplePath, sampleJson, Encoding.UTF8);
            }

            throw new FileNotFoundException(
                $"Brak pliku {ConfigFileName} z danymi dostępowymi do API TEMU. " +
                $"Uzupełnij konfigurację na podstawie pliku {Path.GetFileName(samplePath)}.",
                configPath);
        }

        var json = File.ReadAllText(configPath, Encoding.UTF8);
        var settings = JsonSerializer.Deserialize<TemuApiSettings>(json) ?? new TemuApiSettings();

        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            throw new InvalidOperationException("Konfiguracja TEMU musi zawierać ClientId oraz ClientSecret.");
        }

        if (string.IsNullOrWhiteSpace(settings.ShopId))
        {
            throw new InvalidOperationException("Konfiguracja TEMU musi zawierać identyfikator sklepu ShopId.");
        }

        settings.BaseUrl = settings.BaseUrl.TrimEnd('/') + "/";

        return settings;
    }
}

public sealed class TemuApiClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly TemuApiSettings _settings;
    private bool _disposed;

    public TemuApiClient(TemuApiSettings settings)
    {
        _settings = settings;
        _client = CreateHttpClient(settings);
    }

    public async Task<IReadOnlyList<TemuOrder>> GetNewOrdersAsync(CancellationToken cancellationToken)
    {
        var url = $"orders/new?shopId={Uri.EscapeDataString(_settings.ShopId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var orders = JsonSerializer.Deserialize<TemuOrdersResponse>(json, JsonOptions) ?? new TemuOrdersResponse();
        return orders.Orders;
    }

    public async Task<LabelResult> CreateLabelAsync(string orderId, CancellationToken cancellationToken)
    {
        var url = $"orders/{Uri.EscapeDataString(orderId)}/label";
        var payload = new
        {
            shopId = _settings.ShopId,
            format = _settings.LabelFormat
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var label = JsonSerializer.Deserialize<TemuLabelResponse>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Brak danych etykiety w odpowiedzi API TEMU.");

        if (string.IsNullOrWhiteSpace(label.LabelContentBase64))
        {
            throw new InvalidOperationException("Odpowiedź API TEMU nie zawiera zawartości etykiety.");
        }

        var content = Convert.FromBase64String(label.LabelContentBase64);
        return new LabelResult(orderId, content, label.Format ?? _settings.LabelFormat, label.FileName ?? string.Empty);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException(
                $"Błąd TEMU API ({(int)response.StatusCode} {response.ReasonPhrase}): {errorBody}");
        }

        return response;
    }

    private static HttpClient CreateHttpClient(TemuApiSettings settings)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(settings.BaseUrl)
        };

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ClientId}:{settings.ClientSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyUploader-TEMU-Plugin/1.0");

        return client;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _client.Dispose();
        _disposed = true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class TemuOrdersResponse
{
    public List<TemuOrder> Orders { get; set; } = new();
}

internal sealed class TemuLabelResponse
{
    public string? LabelContentBase64 { get; set; }
    public string? Format { get; set; }
    public string? FileName { get; set; }
}
