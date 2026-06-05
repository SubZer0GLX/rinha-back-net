using System.Text.Json.Serialization;
using Fraud.Core;

namespace Fraud.Api;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(FraudRequest))]
[JsonSerializable(typeof(FraudResponse))]
[JsonSerializable(typeof(NormConstants))]
[JsonSerializable(typeof(Dictionary<string, double>))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
