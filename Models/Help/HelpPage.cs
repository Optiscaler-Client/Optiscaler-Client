using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OptiscalerClient.Models.Help
{
    public class HelpPageConfig
    {
        [JsonPropertyName("pages")]
        public List<HelpPage> Pages { get; set; } = new();
    }

    public class HelpPage
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("titleKey")]
        public string? TitleKey { get; set; }

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = "&#xE8A5;";

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("categoryKey")]
        public string? CategoryKey { get; set; }

        [JsonPropertyName("fontSize")]
        public double? FontSize { get; set; }

        [JsonPropertyName("sections")]
        public List<HelpSection> Sections { get; set; } = new();
    }

    public class HelpSection
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("titleKey")]
        public string? TitleKey { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("contentKey")]
        public string? ContentKey { get; set; }

        [JsonPropertyName("fontSize")]
        public double? FontSize { get; set; }

        [JsonPropertyName("backgroundColor")]
        public string? BackgroundColor { get; set; }

        [JsonPropertyName("textColor")]
        public string? TextColor { get; set; }

        [JsonPropertyName("items")]
        public List<HelpContentItem>? Items { get; set; }
    }

    public class HelpContentItem
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("labelKey")]
        public string? LabelKey { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("textKey")]
        public string? TextKey { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("titleKey")]
        public string? TitleKey { get; set; }

        [JsonPropertyName("fontSize")]
        public double? FontSize { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("linkLabel")]
        public string? LinkLabel { get; set; }

        [JsonPropertyName("linkLabelKey")]
        public string? LinkLabelKey { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("items")]
        public List<HelpContentItem>? Items { get; set; }
    }
}
