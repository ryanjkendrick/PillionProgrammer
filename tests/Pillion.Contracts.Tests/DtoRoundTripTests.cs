using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Resolvers;

namespace Pillion.Contracts.Tests;

/// <summary>
/// Round-trips every DTO through MessagePack-C# with <see cref="StandardResolver"/>, the resolver StreamJsonRpc's
/// MessagePack formatter builds on, to prove the [DataContract]/[DataMember(Order = n)] convention serialises
/// every member without a MessagePack dependency in Pillion.Contracts.
/// </summary>
public class DtoRoundTripTests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance);

    public static TheoryData<string> DtoNames => new(DtoSamples.All.Select(s => s.GetType().Name));

    [Fact]
    public void Samples_CoverEveryDataContractType()
    {
        var expected = typeof(ContractVersion).Assembly.GetExportedTypes()
            .Where(t => t.GetCustomAttribute<DataContractAttribute>() is not null)
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var actual = DtoSamples.All.Select(s => s.GetType().FullName).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(DtoNames))]
    public void Dto_IsSealedWithOrderedDataMembers(string name)
    {
        var type = Sample(name).GetType();

        Assert.True(type.IsSealed, $"{type.Name} must be sealed.");
        Assert.NotNull(type.GetCustomAttribute<DataContractAttribute>());

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var orders = new List<int>();
        foreach (var property in properties)
        {
            var member = property.GetCustomAttribute<DataMemberAttribute>();
            Assert.True(member is not null, $"{type.Name}.{property.Name} must have [DataMember(Order = n)].");
            Assert.True(member.Order >= 0, $"{type.Name}.{property.Name} must set DataMember.Order.");
            orders.Add(member.Order);
        }

        Assert.Equal(Enumerable.Range(0, properties.Length), orders.Order());
    }

    [Theory]
    [MemberData(nameof(DtoNames))]
    public void Sample_PopulatesEveryMember(string name)
    {
        var sample = Sample(name);
        foreach (var property in DataMembers(sample.GetType()))
        {
            var value = property.GetValue(sample);
            Assert.False(IsDefault(value), $"Sample {sample.GetType().Name}.{property.Name} must be set to a non-default value.");
        }
    }

    [Theory]
    [MemberData(nameof(DtoNames))]
    public void Dto_RoundTripsThroughMessagePack(string name)
    {
        var sample = Sample(name);
        var type = sample.GetType();

        var bytes = MessagePackSerializer.Serialize(type, sample, Options);
        var copy = MessagePackSerializer.Deserialize(type, bytes, Options);

        Assert.NotNull(copy);
        Assert.IsType(type, copy);
        AssertDeepEqual(sample, copy, type.Name);
    }

    [Fact]
    public void Dto_IsSerializedAsArrayKeyedByOrder()
    {
        var bytes = MessagePackSerializer.Serialize(new PreviewDecision { ChangesetId = "cs-1", IsAccepted = true }, Options);

        Assert.Equal("[\"cs-1\",true]", MessagePackSerializer.ConvertToJson(bytes, Options));
    }

    private static object Sample(string name) => DtoSamples.All.Single(s => s.GetType().Name == name);

    private static IEnumerable<PropertyInfo> DataMembers(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<DataMemberAttribute>() is not null)
            .OrderBy(p => p.GetCustomAttribute<DataMemberAttribute>()!.Order);

    private static bool IsDefault(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        IEnumerable e => !e.Cast<object?>().Any(),
        _ when value.GetType().IsValueType => value.Equals(Activator.CreateInstance(value.GetType())),
        _ => false,
    };

    private static void AssertDeepEqual(object? expected, object? actual, string path)
    {
        if (expected is null)
        {
            Assert.True(actual is null, $"{path}: expected null but was {actual}.");
            return;
        }

        Assert.True(actual is not null, $"{path}: expected {expected} but was null.");

        var type = expected.GetType();
        if (type.GetCustomAttribute<DataContractAttribute>() is not null)
        {
            Assert.IsType(type, actual);
            foreach (var property in DataMembers(type))
            {
                AssertDeepEqual(property.GetValue(expected), property.GetValue(actual), $"{path}.{property.Name}");
            }

            return;
        }

        if (expected is IEnumerable expectedItems && expected is not string)
        {
            var expectedList = expectedItems.Cast<object?>().ToList();
            var actualList = Assert.IsAssignableFrom<IEnumerable>(actual).Cast<object?>().ToList();
            Assert.True(expectedList.Count == actualList.Count, $"{path}: expected {expectedList.Count} items but was {actualList.Count}.");
            for (var i = 0; i < expectedList.Count; i++)
            {
                AssertDeepEqual(expectedList[i], actualList[i], $"{path}[{i}]");
            }

            return;
        }

        Assert.True(expected.Equals(actual), $"{path}: expected {expected} but was {actual}.");
    }
}
