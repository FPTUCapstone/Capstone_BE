using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripMate.Application.Common.Serialization;

public sealed class StrictStringEnumJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        var enumType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
        return enumType.IsEnum;
    }

    public override JsonConverter CreateConverter(
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var enumType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;

        if (!enumType.IsEnum)
        {
            throw new ArgumentException("The converter can only be used with enum types.", nameof(typeToConvert));
        }

        var converterType = Nullable.GetUnderlyingType(typeToConvert) is null
            ? typeof(StrictStringEnumJsonConverter<>).MakeGenericType(enumType)
            : typeof(StrictNullableStringEnumJsonConverter<>).MakeGenericType(enumType);

        return (JsonConverter)(Activator.CreateInstance(converterType, nonPublic: true)
            ?? throw new InvalidOperationException($"Unable to create a converter for {typeToConvert}."));
    }

    private static TEnum ReadEnum<TEnum>(ref Utf8JsonReader reader)
        where TEnum : struct, Enum
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"{typeof(TEnum).Name} must be provided as a string name.");
        }

        var value = reader.GetString();
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        throw new JsonException($"'{value}' is not a valid {typeof(TEnum).Name} name.");
    }

    private static void WriteEnum<TEnum>(Utf8JsonWriter writer, TEnum value)
        where TEnum : struct, Enum
    {
        var name = Enum.GetName(value)
            ?? throw new JsonException($"'{value}' is not a defined {typeof(TEnum).Name} value.");
        writer.WriteStringValue(name);
    }

    private sealed class StrictStringEnumJsonConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            ReadEnum<TEnum>(ref reader);

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options) =>
            WriteEnum(writer, value);
    }

    private sealed class StrictNullableStringEnumJsonConverter<TEnum> : JsonConverter<TEnum?>
        where TEnum : struct, Enum
    {
        public override bool HandleNull => true;

        public override TEnum? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : ReadEnum<TEnum>(ref reader);

        public override void Write(
            Utf8JsonWriter writer,
            TEnum? value,
            JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            WriteEnum(writer, value.Value);
        }
    }
}