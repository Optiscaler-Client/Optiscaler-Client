using System;
using System.Collections.Generic;

namespace OptiscalerClient.Models
{
    public class OptiScalerProfile
    {
        public const string BuiltInDefaultName = "OptiScaler Standard";
        public const string BuiltInFsr4Name = "FSR 4";
        public const string BuiltInFsr4Int8Name = "FSR 4 (INT8)";
        public string Name { get; set; } = BuiltInDefaultName;
        public string Description { get; set; } = "";
        public bool IsBuiltIn { get; set; } = false;
        public string CreatedBy { get; set; } = "User";
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        
        public Dictionary<string, Dictionary<string, string>> IniSettings { get; set; } = new();

        public OptiScalerProfile Clone()
        {
            var clone = new OptiScalerProfile
            {
                Name = Name,
                Description = Description,
                IsBuiltIn = false,
                CreatedBy = CreatedBy,
                CreatedDate = DateTime.Now
            };

            foreach (var section in IniSettings)
            {
                clone.IniSettings[section.Key] = new Dictionary<string, string>(section.Value);
            }

            return clone;
        }

        public static OptiScalerProfile CreateDefault()
        {
            return new OptiScalerProfile
            {
                Name = BuiltInDefaultName,
                Description = "Uses OptiScaler's standard configuration (no custom INI)",
                IsBuiltIn = true,
                CreatedBy = "System",
                IniSettings = new Dictionary<string, Dictionary<string, string>>()
            };
        }

        public static OptiScalerProfile CreateFsr4()
        {
            return new OptiScalerProfile
            {
                Name = BuiltInFsr4Name,
                Description = "Forces the FSR 3.1/4 upscaler backend, for GPUs with native FSR4 support (RDNA4/RDNA3)",
                IsBuiltIn = true,
                CreatedBy = "System",
                IniSettings = new Dictionary<string, Dictionary<string, string>>
                {
                    ["Upscalers"] = new Dictionary<string, string>
                    {
                        ["Dx11Upscaler"] = "fsr31_12",
                        ["Dx12Upscaler"] = "fsr31",
                        ["VulkanUpscaler"] = "fsr31_12",
                    }
                }
            };
        }

        public static OptiScalerProfile CreateFsr4Int8()
        {
            return new OptiScalerProfile
            {
                Name = BuiltInFsr4Int8Name,
                Description = "Forces the FSR4 INT8 software fallback, for GPUs without native FSR4 support",
                IsBuiltIn = true,
                CreatedBy = "System",
                IniSettings = new Dictionary<string, Dictionary<string, string>>
                {
                    ["Upscalers"] = new Dictionary<string, string>
                    {
                        ["Dx11Upscaler"] = "fsr31_12",
                        ["Dx12Upscaler"] = "fsr31",
                        ["VulkanUpscaler"] = "fsr31_12",
                    },
                    ["FSR"] = new Dictionary<string, string>
                    {
                        ["UpscalerIndex"] = "0",
                        ["Fsr4ForceModel"] = "2",
                        ["Fsr4ForceEnableInt8"] = "true",
                        ["Fsr4Update"] = "true",
                    }
                }
            };
        }

        public static OptiScalerProfile CreateEmpty()
        {
            return new OptiScalerProfile
            {
                Name = "New Profile",
                Description = "",
                IsBuiltIn = false,
                CreatedBy = "User",
                CreatedDate = DateTime.Now,
                IniSettings = new Dictionary<string, Dictionary<string, string>>()
            };
        }
    }
}
