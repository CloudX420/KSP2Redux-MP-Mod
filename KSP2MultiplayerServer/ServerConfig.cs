using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSP2MultiplayerServer
{
    public class ServerConfig
    {
        public List<string> AdminSteamIDs { get; set; } = new List<string>();

        public static ServerConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                var config = new ServerConfig();
                config.AdminSteamIDs.Add("12345678901234567"); // Example dummy ID
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                return config;
            }

            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<ServerConfig>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Error loading {path}: {ex.Message}");
                return new ServerConfig();
            }
        }
    }
}
