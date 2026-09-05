using System;
using System.Collections.Generic;

namespace OptiscalerClient.Models
{
    public class OptiScalerProfile
    {
        public const string BuiltInDefaultName = "OptiScaler Standard";
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
