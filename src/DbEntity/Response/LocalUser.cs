using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DbEntity
{
    public class LocalUserResponse
    {
        [JsonProperty("code")]
        public int Code { get; set; }
        [JsonProperty("subcode")]
        public int Subcode { get; set; }
        [JsonProperty("result")]
        public LocalUser Result { get; set; }
    }

    public class LocalUser
    {
        [JsonProperty("nick")]
        public string Nick { get; set; }
        [JsonProperty("targetId")]
        public string TargetId { get; set; }
        [JsonProperty("display")]
        public string Display { get; set; }
    }

    public class ConversationResponse
    {
        [JsonProperty("code")]
        public int Code { get; set; }
        [JsonProperty("subcode")]
        public int Subcode { get; set; }
        [JsonProperty("result")]
        public Conversation Result { get; set; }
    }

    public class Conversation
    {
        [JsonProperty("nick")]
        public string Nick { get; set; }
        [JsonProperty("display")]
        public string Display { get; set; }
        [JsonProperty("ccode")]
        public string Ccode { get; set; }
        [JsonProperty("targetId")]
        public string TargetId { get; set; }
        [JsonProperty("targetType")]
        public string TargetType { get; set; }
        [JsonProperty("type")]
        public string Type { get; set; }
        [JsonProperty("conversationType")]
        public string ConversationType { get; set; }
        [JsonProperty("scene")]
        public string Scene { get; set; }
        [JsonProperty("category")]
        public string Category { get; set; }
        [JsonProperty("source")]
        public string Source { get; set; }
        [JsonProperty("channel")]
        public string Channel { get; set; }
    }

    public class ActiveLocalUser
    {
        [JsonProperty("code")]
        public int Code { get; set; }
        [JsonProperty("subcode")]
        public int Subcode { get; set; }
        [JsonProperty("loginID")]
        public LocalUser LoginID { get; set; }
        [JsonProperty("conversation")]
        public Conversation Conversation { get; set; }
    }
}
