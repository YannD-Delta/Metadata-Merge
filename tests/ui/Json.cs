/// <summary>Playnite saves plugin settings with Newtonsoft.Json and honours its own [DontSerialize]; this does the same (Playnite's static serializer needs a running Playnite).</summary>
public static class Json
{
    private sealed class Resolver : Newtonsoft.Json.Serialization.DefaultContractResolver
    {
        protected override Newtonsoft.Json.Serialization.JsonProperty CreateProperty(System.Reflection.MemberInfo member, Newtonsoft.Json.MemberSerialization ms)
        {
            var p = base.CreateProperty(member, ms);
            if (member.GetCustomAttributes(typeof(Playnite.SDK.Data.DontSerializeAttribute), true).Length > 0) p.Ignored = true;
            return p;
        }
    }

    private static readonly Newtonsoft.Json.JsonSerializerSettings Settings = new Newtonsoft.Json.JsonSerializerSettings { ContractResolver = new Resolver() };
    public static string ToJson(object o) { return Newtonsoft.Json.JsonConvert.SerializeObject(o, Settings); }
    public static T FromJson<T>(string json) { return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(json, Settings); }
}
