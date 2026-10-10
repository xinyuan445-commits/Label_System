using U8Gateway.Contracts.Metadata;

namespace U8Gateway.Core.Documents
{
    public interface IU8XmlCodec
    {
        U8XmlPacket Encode(
            DocumentDefinition definition,
            DocumentPayload payload);

        DocumentPayload Decode(
            DocumentDefinition definition,
            U8XmlPacket packet);
    }

    public sealed class U8XmlPacket
    {
        public string HeaderXml { get; set; }
        public string BodyXml { get; set; }
        public string PositionXml { get; set; }
    }
}
