using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Menu;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AdminPlus;

public partial class AdminPlus
{
    private sealed record PlayerGeoInfo(string Flag, string Country, string Region, string City);

    private static readonly HttpClient PlayerCheckerHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private static readonly ConcurrentDictionary<string, (PlayerGeoInfo Info, DateTime Expires)> PlayerGeoCache = new();

    private void ShowPlayerCheckerMenu(CCSPlayerController admin)
    {
        if (!admin.IsValid || !HasEffectivePermission(admin, "@css/root"))
        {
            admin.Print(Localizer["NoPermission"]);
            return;
        }

        var menu = CreateMenu("Чекер игрока — выбери игрока");
        if (menu == null) return;

        foreach (var target in Utilities.GetPlayers().Where(p => p != null && p.IsValid && !p.IsBot))
        {
            var player = target;
            var label = $"{SanitizeName(player.PlayerName)} [#{player.UserId}]";
            menu.AddMenuOption(label, (_, _) => ShowPlayerCheckerDetails(admin, player));
        }

        if (!menu.MenuOptions.Any())
            menu.AddMenuOption("Нет игроков онлайн", (_, _) => { });

        menu.ExitButton = true;
        OpenMenu(admin, menu);
    }

    private void ShowPlayerCheckerDetails(CCSPlayerController admin, CCSPlayerController target)
    {
        if (!admin.IsValid || !HasEffectivePermission(admin, "@css/root"))
        {
            admin.Print(Localizer["NoPermission"]);
            return;
        }

        if (target == null || !target.IsValid || target.IsBot)
        {
            admin.Print("Игрок уже отключился.");
            return;
        }

        var playerName = SanitizeName(target.PlayerName);
        var steamId = target.SteamID.ToString();
        var ip = NormalizeIpAddress(target.IpAddress ?? string.Empty);

        var loading = CreateMenu("Чекер игрока — " + playerName);
        if (loading == null) return;

        loading.AddMenuOption("Игрок: " + playerName, (_, _) => { }, true);
        loading.AddMenuOption("SteamID: " + steamId, (_, _) => { }, true);
        loading.AddMenuOption("IP: " + (string.IsNullOrWhiteSpace(ip) ? "недоступен" : ip), (_, _) => { }, true);
        loading.AddMenuOption("Геолокация: загружается...", (_, _) => { }, true);
        loading.ExitButton = true;
        OpenMenu(admin, loading);

        if (!IPAddress.TryParse(ip, out var address) || IsNonPublicAddress(address))
        {
            ShowPlayerCheckerResult(admin, playerName, steamId, ip, null,
                string.IsNullOrWhiteSpace(ip) ? "IP недоступен" : "Локальный/служебный IP — геолокация не определяется");
            return;
        }

        if (PlayerGeoCache.TryGetValue(ip, out var cached) && cached.Expires > DateTime.UtcNow)
        {
            ShowPlayerCheckerResult(admin, playerName, steamId, ip, cached.Info, null);
            return;
        }

        _ = Task.Run(async () =>
        {
            PlayerGeoInfo? geo = null;
            string? error = null;

            try
            {
                var url = $"https://ipwho.is/{Uri.EscapeDataString(ip)}?lang=ru&fields=success,country,country_code,region,city,flag";
                using var response = await PlayerCheckerHttp.GetAsync(url).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
                var root = document.RootElement;

                if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
                    throw new InvalidOperationException("GeoIP сервис не смог определить адрес.");

                var country = ReadJsonString(root, "country");
                var region = ReadJsonString(root, "region");
                var city = ReadJsonString(root, "city");
                var countryCode = ReadJsonString(root, "country_code");
                var flag = root.TryGetProperty("flag", out var flagElement)
                    ? ReadJsonString(flagElement, "emoji")
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(flag) && countryCode.Length == 2)
                    flag = CountryCodeToFlag(countryCode);

                geo = new PlayerGeoInfo(
                    string.IsNullOrWhiteSpace(flag) ? "🌐" : flag,
                    string.IsNullOrWhiteSpace(country) ? "Не определена" : country,
                    region,
                    city);

                PlayerGeoCache[ip] = (geo, DateTime.UtcNow.AddHours(24));
            }
            catch (Exception ex)
            {
                error = "Не удалось получить геолокацию: " + ex.Message;
                LogError($"Player checker GeoIP lookup failed: {ex.Message}");
            }

            Server.NextFrame(() =>
            {
                if (admin != null && admin.IsValid)
                    ShowPlayerCheckerResult(admin, playerName, steamId, ip, geo, error);
            });
        });
    }

    private void ShowPlayerCheckerResult(
        CCSPlayerController admin,
        string playerName,
        string steamId,
        string ip,
        PlayerGeoInfo? geo,
        string? error)
    {
        if (!admin.IsValid || !HasEffectivePermission(admin, "@css/root"))
        {
            admin.Print(Localizer["NoPermission"]);
            return;
        }

        var menu = CreateMenu("Информация об игроке");
        if (menu == null) return;

        menu.AddMenuOption("Игрок: " + playerName, (_, _) => { }, true);
        menu.AddMenuOption("SteamID: " + steamId, (_, _) => { }, true);
        menu.AddMenuOption("IP: " + (string.IsNullOrWhiteSpace(ip) ? "недоступен" : ip), (_, _) => { }, true);

        if (geo != null)
        {
            menu.AddMenuOption($"{geo.Flag} Страна: {geo.Country}", (_, _) => { }, true);
            menu.AddMenuOption("Регион: " + (string.IsNullOrWhiteSpace(geo.Region) ? "не определён" : geo.Region), (_, _) => { }, true);
            menu.AddMenuOption("Город: " + (string.IsNullOrWhiteSpace(geo.City) ? "не определён" : geo.City), (_, _) => { }, true);
            menu.AddMenuOption("Геолокация приблизительная (по IP)", (_, _) => { }, true);
        }
        else
        {
            menu.AddMenuOption("Геолокация: " + (error ?? "не определена"), (_, _) => { }, true);
        }

        menu.ExitButton = true;
        OpenMenu(admin, menu);
    }

    private static string ReadJsonString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool IsNonPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return true;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                || bytes[0] >= 224;
        }

        return address.IsIPv6UniqueLocal;
    }

    private static string CountryCodeToFlag(string countryCode)
    {
        if (countryCode.Length != 2 || !countryCode.All(char.IsLetter))
            return "🌐";

        return char.ConvertFromUtf32(0x1F1E6 + char.ToUpperInvariant(countryCode[0]) - 'A')
            + char.ConvertFromUtf32(0x1F1E6 + char.ToUpperInvariant(countryCode[1]) - 'A');
    }
}
