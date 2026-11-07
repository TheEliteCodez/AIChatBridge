using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChromeSidecarStandalone.Util
{
    public static class UtilCompat
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        public static string JsonSerialize<T>(T obj)
        {
            return JsonConvert.SerializeObject(obj, Settings);
        }

        public static T JsonDeserialize<T>(string json)
        {
            if (string.IsNullOrEmpty(json)) return default(T);
            try
            {
                var token = JsonConvert.DeserializeObject(json);
                var normalized = NormalizeToken(token);
                if (typeof(T) == typeof(Dictionary<string, object>)
                    || typeof(T) == typeof(List<object>))
                    return (T)normalized;
                return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(normalized), Settings);
            }
            catch
            {
                return default(T);
            }
        }

        public static T JsonDeserializeStrictUtc<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("JSON input is required.");

            var token = JsonConvert.DeserializeObject(json);
            var normalized = NormalizeToken(token);
            if (typeof(T) == typeof(Dictionary<string, object>)
                || typeof(T) == typeof(List<object>))
                return (T)normalized;
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(normalized), Settings);
        }

        private static object NormalizeToken(object value)
        {
            var token = value as JToken;
            if (token is JObject obj)
            {
                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in obj.Properties())
                    result[property.Name] = NormalizeToken(property.Value);
                return result;
            }

            if (token is JArray array)
            {
                var result = new List<object>();
                foreach (var item in array)
                    result.Add(NormalizeToken(item));
                return result;
            }

            if (token is JValue scalar)
                return scalar.Value;

            return value;
        }
    }
}
