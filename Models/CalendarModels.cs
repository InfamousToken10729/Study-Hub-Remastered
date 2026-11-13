using System;
using System.Text.Json.Serialization;

namespace StudyHub.Models
{
    public class CalendarEventItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("note")]
        public string Note { get; set; } = "";

        [JsonPropertyName("date")]
        public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");

        [JsonPropertyName("eventTime")]
        public string EventTime { get; set; } = "09:00";

        [JsonPropertyName("subjectId")]
        public string SubjectId { get; set; } = "";

        [JsonPropertyName("notify3DaysBefore")]
        public bool Notify3DaysBefore { get; set; } = true;

        [JsonPropertyName("notify24HoursBefore")]
        public bool Notify24HoursBefore { get; set; } = true;

        [JsonPropertyName("notifyMorningOf")]
        public bool NotifyMorningOf { get; set; } = true;

        [JsonPropertyName("notifyCustom")]
        public bool NotifyCustom { get; set; } = false;

        [JsonPropertyName("customOffsetMinutes")]
        public int CustomOffsetMinutes { get; set; } = 120;

        [JsonPropertyName("customTriggerIso")]
        public string CustomTriggerIso { get; set; } = "";

        [JsonPropertyName("completed")]
        public bool Completed { get; set; } = false;

        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");
    }
}
