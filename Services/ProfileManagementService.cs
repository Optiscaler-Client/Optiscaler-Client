using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services
{
    public class ProfileManagementService
    {
        private readonly string _profilesDir;
        private readonly string _builtInProfilesDir;
        private readonly string _customProfilesDir;

        private List<OptiScalerProfile> _cachedProfiles = new();
        private DateTime _lastCacheTime = DateTime.MinValue;

        public ProfileManagementService()
        {
            _profilesDir = Path.Combine(AppPaths.GetAppDataRoot(), "Profiles");
            _builtInProfilesDir = Path.Combine(_profilesDir, "builtin");
            _customProfilesDir = Path.Combine(_profilesDir, "custom");

            Directory.CreateDirectory(_profilesDir);
            Directory.CreateDirectory(_builtInProfilesDir);
            Directory.CreateDirectory(_customProfilesDir);

            InitializeBuiltInProfiles();
        }

        private void InitializeBuiltInProfiles()
        {
            var defaultProfile = OptiScalerProfile.CreateDefault();
            var profilePath = Path.Combine(_builtInProfilesDir, $"{SanitizeFileName(defaultProfile.Name)}.json");
            var legacyProfilePath = Path.Combine(_builtInProfilesDir, "Default.json");

            if (!File.Exists(profilePath))
            {
                if (File.Exists(legacyProfilePath))
                {
                    var legacyProfile = LoadProfileFromFile(legacyProfilePath);
                    if (legacyProfile != null)
                    {
                        legacyProfile.Name = defaultProfile.Name;
                        legacyProfile.Description = defaultProfile.Description;
                        SaveProfile(legacyProfile, isBuiltIn: true);
                        File.Delete(legacyProfilePath);
                        DebugWindow.Log($"[Profiles] Migrated legacy default profile to: {defaultProfile.Name}");
                        return;
                    }
                }

                SaveProfile(defaultProfile, isBuiltIn: true);
                DebugWindow.Log($"[Profiles] Created built-in profile: {defaultProfile.Name}");
            }
        }

        public List<OptiScalerProfile> GetAllProfiles(bool forceRefresh = false)
        {
            if (!forceRefresh && (DateTime.Now - _lastCacheTime).TotalSeconds < 5 && _cachedProfiles.Count > 0)
            {
                return _cachedProfiles;
            }

            var profiles = new List<OptiScalerProfile>();

            try
            {
                var builtInFiles = Directory.GetFiles(_builtInProfilesDir, "*.json");
                foreach (var file in builtInFiles)
                {
                    try
                    {
                        var profile = LoadProfileFromFile(file);
                        if (profile != null)
                        {
                            profile.IsBuiltIn = true;
                            profiles.Add(profile);
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Profiles] Failed to load built-in profile {file}: {ex.Message}");
                    }
                }

                var customFiles = Directory.GetFiles(_customProfilesDir, "*.json");
                foreach (var file in customFiles)
                {
                    try
                    {
                        var profile = LoadProfileFromFile(file);
                        if (profile != null)
                        {
                            profile.IsBuiltIn = false;
                            profiles.Add(profile);
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Profiles] Failed to load custom profile {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Profiles] Error loading profiles: {ex.Message}");
            }

            profiles = profiles.OrderBy(p => p.IsBuiltIn ? 0 : 1).ThenBy(p => p.Name).ToList();

            _cachedProfiles = profiles;
            _lastCacheTime = DateTime.Now;

            return profiles;
        }

        public OptiScalerProfile? GetProfileByName(string name)
        {
            var profiles = GetAllProfiles();
            return profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public OptiScalerProfile? LoadProfileFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                var json = File.ReadAllText(filePath);
                var profile = JsonSerializer.Deserialize(json, OptimizerContext.Default.OptiScalerProfile);
                return profile;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Profiles] Failed to deserialize profile from {filePath}: {ex.Message}");
                return null;
            }
        }

        public void SaveProfile(OptiScalerProfile profile, bool isBuiltIn = false)
        {
            try
            {
                var targetDir = isBuiltIn ? _builtInProfilesDir : _customProfilesDir;
                var fileName = $"{SanitizeFileName(profile.Name)}.json";
                var filePath = Path.Combine(targetDir, fileName);

                var json = JsonSerializer.Serialize(profile, OptimizerContext.Default.OptiScalerProfile);
                File.WriteAllText(filePath, json);

                _lastCacheTime = DateTime.MinValue;
                DebugWindow.Log($"[Profiles] Saved profile: {profile.Name} to {filePath}");
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Profiles] Failed to save profile {profile.Name}: {ex.Message}");
                throw;
            }
        }

        public void DeleteProfile(OptiScalerProfile profile)
        {
            if (profile.IsBuiltIn)
            {
                throw new InvalidOperationException("Cannot delete built-in profiles");
            }

            try
            {
                var fileName = $"{SanitizeFileName(profile.Name)}.json";
                var filePath = Path.Combine(_customProfilesDir, fileName);

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _lastCacheTime = DateTime.MinValue;
                    DebugWindow.Log($"[Profiles] Deleted profile: {profile.Name}");
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Profiles] Failed to delete profile {profile.Name}: {ex.Message}");
                throw;
            }
        }

        private static string? _embeddedTemplate;

        private static string? TryGetEmbeddedTemplate()
        {
            if (_embeddedTemplate != null) return _embeddedTemplate;
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                // Resource name: <AssemblyName>.<FileName>
                var resourceName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("OptiScaler_example.ini", StringComparison.OrdinalIgnoreCase));
                if (resourceName == null) return null;
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null) return null;
                using var reader = new StreamReader(stream);
                _embeddedTemplate = reader.ReadToEnd();
                return _embeddedTemplate;
            }
            catch
            {
                return null;
            }
        }

        public string GenerateOptiScalerIni(OptiScalerProfile profile, string templatePath)
        {
            // Try disk file first, then embedded resource, then legacy fallback
            string? templateContent = null;
            if (File.Exists(templatePath))
            {
                try { templateContent = File.ReadAllText(templatePath); }
                catch (Exception ex) { DebugWindow.Log($"[Profiles] Failed to read template '{templatePath}': {ex.Message}"); }
            }
            templateContent ??= TryGetEmbeddedTemplate();

            if (templateContent == null)
            {
                DebugWindow.Log("[Profiles] Warning: No OptiScaler.ini template available, using legacy generation.");
                return GenerateOptiScalerIniLegacy(profile);
            }

            try
            {
                var lines = templateContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var modifiedLines = new List<string>();
                string? currentSection = null;

                // Add header comment
                modifiedLines.Add($"; OptiScaler Configuration - Generated from profile: {profile.Name}");
                modifiedLines.Add($"; {profile.Description}");
                modifiedLines.Add($"; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                modifiedLines.Add("");

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    // Track current section
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2);
                        modifiedLines.Add(line);
                        continue;
                    }

                    // Check if this is a setting line (key=value)
                    if (!string.IsNullOrWhiteSpace(trimmed) &&
                        !trimmed.StartsWith(";") &&
                        trimmed.Contains("=") &&
                        currentSection != null)
                    {
                        var parts = trimmed.Split(new[] { '=' }, 2);
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim();

                            // Check if this setting is configured in the profile
                            if (profile.IniSettings.ContainsKey(currentSection) &&
                                profile.IniSettings[currentSection].ContainsKey(key))
                            {
                                var newValue = profile.IniSettings[currentSection][key];
                                modifiedLines.Add($"{key}={newValue}");
                                continue;
                            }
                        }
                    }

                    // Keep the line as-is (comments, empty lines, unconfigured settings)
                    modifiedLines.Add(line);
                }

                return string.Join(Environment.NewLine, modifiedLines);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Profiles] Error using template: {ex.Message}. Falling back to legacy generation.");
                return GenerateOptiScalerIniLegacy(profile);
            }
        }

        private string GenerateOptiScalerIniLegacy(OptiScalerProfile profile)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; OptiScaler Configuration");
            sb.AppendLine($"; Generated from profile: {profile.Name}");
            sb.AppendLine($"; {profile.Description}");
            sb.AppendLine($"; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            foreach (var section in profile.IniSettings.OrderBy(s => s.Key))
            {
                sb.AppendLine($"[{section.Key}]");

                foreach (var setting in section.Value.OrderBy(s => s.Key))
                {
                    sb.AppendLine($"{setting.Key}={setting.Value}");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        public void WriteOptiScalerIniToFile(string gameDir, OptiScalerProfile profile)
        {
            var iniPath = Path.Combine(gameDir, "OptiScaler.ini");
            var iniContent = GenerateOptiScalerIni(profile, iniPath);

            try
            {
                File.WriteAllText(iniPath, iniContent);
                DebugWindow.Log($"[Profiles] Generated OptiScaler.ini from profile '{profile.Name}' at {iniPath}");
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Profiles] Failed to write OptiScaler.ini: {ex.Message}");
                throw;
            }
        }

        public OptiScalerProfile CreateProfileFromIni(string iniPath, string profileName, string description = "")
        {
            if (!File.Exists(iniPath))
                throw new FileNotFoundException($"INI file not found: {iniPath}");

            var profile = new OptiScalerProfile
            {
                Name = profileName,
                Description = description,
                IsBuiltIn = false,
                CreatedBy = "User",
                CreatedDate = DateTime.Now
            };

            try
            {
                var lines = File.ReadAllLines(iniPath);
                string? currentSection = null;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith(";"))
                        continue;

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2);
                        if (!profile.IniSettings.ContainsKey(currentSection))
                        {
                            profile.IniSettings[currentSection] = new Dictionary<string, string>();
                        }
                        continue;
                    }

                    if (currentSection != null && trimmed.Contains("="))
                    {
                        var parts = trimmed.Split(new[] { '=' }, 2);
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim();
                            var value = parts[1].Trim();
                            profile.IniSettings[currentSection][key] = value;
                        }
                    }
                }

                DebugWindow.Log($"[Profiles] Created profile '{profileName}' from INI file with {profile.IniSettings.Count} sections");
                return profile;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Profiles] Failed to parse INI file: {ex.Message}");
                throw;
            }
        }

        public OptiScalerProfile GetDefaultProfile()
        {
            return GetProfileByName(OptiScalerProfile.BuiltInDefaultName)
                ?? GetProfileByName("Default")
                ?? OptiScalerProfile.CreateDefault();
        }

        private string SanitizeFileName(string fileName)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
            return sanitized;
        }

        public string GetProfilesDirectory() => _profilesDir;
        public string GetCustomProfilesDirectory() => _customProfilesDir;
    }
}
