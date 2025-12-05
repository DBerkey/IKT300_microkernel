using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IKT300.Shared.Configuration
{
    public class KernelConfig
    {
        private const string DefaultConfigFileName = "kernelconfig.json";
        private const string DefaultHost = "127.0.0.1";
        private const int DefaultPort = 9000;

        public string Host { get; set; } = DefaultHost;
        public int Port { get; set; } = DefaultPort;
        public List<PluginConfig> Plugins { get; set; } = new();

        [JsonIgnore]
        public string ConfigDirectory { get; private set; } = Directory.GetCurrentDirectory();

        [JsonIgnore]
        public string ConfigPath { get; private set; } = string.Empty;

        public static KernelConfig Load(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Kernel config file not found: {path}");
            }

            var json = File.ReadAllText(fullPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var config = JsonSerializer.Deserialize<KernelConfig>(json, options)
                ?? throw new InvalidDataException("Unable to deserialize kernel configuration file.");

            config.ConfigPath = fullPath;
            config.ConfigDirectory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            foreach (var plugin in config.Plugins)
            {
                plugin.Normalize();
                plugin.Validate();
            }

            return config;
        }

        public static KernelConfig LoadFromDefaultLocations(string? overridePath = null, string fileName = DefaultConfigFileName)
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return Load(Path.GetFullPath(overridePath));
            }

            var resolvedPath = FindConfigFilePath(fileName);
            return Load(resolvedPath);
        }

        public static string FindConfigFilePath(string fileName = DefaultConfigFileName)
        {
            var searchDirectories = EnumerateSearchDirectories().ToList();
            foreach (var directory in searchDirectories)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var message = $"Could not locate {fileName}. Checked: {string.Join(", ", searchDirectories)}";
            throw new FileNotFoundException(message);
        }

        private static IEnumerable<string> EnumerateSearchDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var seed in GetSeedDirectories())
            {
                if (string.IsNullOrWhiteSpace(seed))
                {
                    continue;
                }

                var current = Path.GetFullPath(seed);
                while (!string.IsNullOrWhiteSpace(current))
                {
                    if (seen.Add(current))
                    {
                        yield return current;
                    }

                    current = Directory.GetParent(current)?.FullName;
                }
            }
        }

        private static IEnumerable<string?> GetSeedDirectories()
        {
            yield return AppContext.BaseDirectory;
            yield return Directory.GetCurrentDirectory();
            yield return Path.GetDirectoryName(typeof(KernelConfig).Assembly.Location);
        }
    }

    public class PluginConfig
    {
        public string PluginId { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;
        public string? DllRelativePath { get; set; }
        public bool Enabled { get; set; } = true;
        public bool AutoStart { get; set; } = true;
        public int ExitAfterSeconds { get; set; } = 0;

        [JsonIgnore]
        public string NormalizedPluginId => PluginId ?? string.Empty;

        public void Normalize()
        {
            PluginId = PluginId?.Trim() ?? string.Empty;
            WorkingDirectory = WorkingDirectory?.Trim() ?? string.Empty;
            DllRelativePath = string.IsNullOrWhiteSpace(DllRelativePath) ? null : DllRelativePath.Trim();
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(PluginId))
            {
                throw new InvalidDataException("Plugin configuration is missing 'pluginId'.");
            }

            if (string.IsNullOrWhiteSpace(WorkingDirectory))
            {
                throw new InvalidDataException($"Plugin '{PluginId}' is missing 'workingDirectory'.");
            }
        }
    }
}
