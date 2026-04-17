using OrleansReplicaKernel.CodeGeneration;
using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Tests.Serialization;

[GenerateSerializer]
public sealed record SimpleRecord(
    [property: Id(1)] string Name,
    [property: Id(2)] int Age);

[GenerateSerializer]
public sealed record RecordWithNullableFields(
    [property: Id(1)] string Label,
    [property: Id(2)] string? Description,
    [property: Id(3)] int? OptionalCount);

[GenerateSerializer]
public sealed record RecordWithCollections(
    [property: Id(1)] string Key,
    [property: Id(2)] List<int> Numbers,
    [property: Id(3)] Dictionary<string, string> Tags);

[GenerateSerializer]
public sealed class PocoClass
{
    [Id(1)]
    public string Title { get; set; } = string.Empty;

    [Id(2)]
    public double Score { get; set; }

    [Id(3)]
    public bool IsActive { get; set; }
}

[GenerateSerializer]
public struct SimpleStruct
{
    [Id(1)]
    public int X { get; set; }

    [Id(2)]
    public int Y { get; set; }
}

[GenerateSerializer]
public sealed record NestedRecord(
    [property: Id(1)] string Id,
    [property: Id(2)] SimpleRecord Inner);

public interface IShape
{
    double Area { get; }
}

[GenerateSerializer]
public sealed record Circle([property: Id(1)] double Radius) : IShape
{
    public double Area => Math.PI * Radius * Radius;
}

[GenerateSerializer]
public sealed record Rectangle([property: Id(1)] double Width, [property: Id(2)] double Height) : IShape
{
    public double Area => Width * Height;
}

[GenerateSerializer]
public sealed record ShapeContainer(
    [property: Id(1)] string Label,
    [property: Id(2)] IShape Shape);

[GenerateSerializer]
public sealed record MultiShapeContainer(
    [property: Id(1)] string Label,
    [property: Id(2)] IShape Primary,
    [property: Id(3)] IShape? Secondary);

public abstract class Animal
{
    public abstract string Sound { get; }
}

[GenerateSerializer]
public sealed class Dog : Animal
{
    [Id(1)]
    public string Name { get; set; } = string.Empty;

    public override string Sound => "Woof";
}

[GenerateSerializer]
public sealed class Cat : Animal
{
    [Id(1)]
    public string Name { get; set; } = string.Empty;

    public override string Sound => "Meow";
}

[GenerateSerializer]
public sealed record PetOwner(
    [property: Id(1)] string OwnerName,
    [property: Id(2)] Animal Pet);

public sealed class GeneratedSerializerTests
{
    [Fact]
    public void SimpleRecord_RoundTrips()
    {
        var serializer = CreateSerializer();
        var original = new SimpleRecord("Alice", 30);

        var restored = serializer.Deserialize<SimpleRecord>(serializer.Serialize(original));

        Assert.Equal("Alice", restored.Name);
        Assert.Equal(30, restored.Age);
    }

    [Fact]
    public void RecordWithNullableFields_RoundTrips_WithValues()
    {
        var serializer = CreateSerializer();
        var original = new RecordWithNullableFields("test", "some description", 42);

        var restored = serializer.Deserialize<RecordWithNullableFields>(serializer.Serialize(original));

        Assert.Equal("test", restored.Label);
        Assert.Equal("some description", restored.Description);
        Assert.Equal(42, restored.OptionalCount);
    }

    [Fact]
    public void RecordWithNullableFields_RoundTrips_WithNulls()
    {
        var serializer = CreateSerializer();
        var original = new RecordWithNullableFields("test", null, null);

        var restored = serializer.Deserialize<RecordWithNullableFields>(serializer.Serialize(original));

        Assert.Equal("test", restored.Label);
        Assert.Null(restored.Description);
        Assert.Null(restored.OptionalCount);
    }

    [Fact]
    public void RecordWithCollections_RoundTrips()
    {
        var serializer = CreateSerializer();
        var original = new RecordWithCollections(
            "collection-key",
            [1, 2, 3, 4, 5],
            new Dictionary<string, string>
            {
                ["env"] = "prod",
                ["region"] = "us-west"
            });

        var restored = serializer.Deserialize<RecordWithCollections>(serializer.Serialize(original));

        Assert.Equal("collection-key", restored.Key);
        Assert.Equal([1, 2, 3, 4, 5], restored.Numbers);
        Assert.Equal("prod", restored.Tags["env"]);
        Assert.Equal("us-west", restored.Tags["region"]);
    }

    [Fact]
    public void PocoClass_RoundTrips_WithObjectInitializer()
    {
        var serializer = CreateSerializer();
        var original = new PocoClass
        {
            Title = "Generated Codec Test",
            Score = 99.5,
            IsActive = true
        };

        var restored = serializer.Deserialize<PocoClass>(serializer.Serialize(original));

        Assert.Equal("Generated Codec Test", restored.Title);
        Assert.Equal(99.5, restored.Score);
        Assert.True(restored.IsActive);
    }

    [Fact]
    public void SimpleStruct_RoundTrips()
    {
        var serializer = CreateSerializer();
        var original = new SimpleStruct { X = 10, Y = 20 };

        var restored = serializer.Deserialize<SimpleStruct>(serializer.Serialize(original));

        Assert.Equal(10, restored.X);
        Assert.Equal(20, restored.Y);
    }

    [Fact]
    public void NestedRecord_RoundTrips()
    {
        var serializer = CreateSerializer();
        var original = new NestedRecord("outer", new SimpleRecord("inner", 7));

        var restored = serializer.Deserialize<NestedRecord>(serializer.Serialize(original));

        Assert.Equal("outer", restored.Id);
        Assert.Equal("inner", restored.Inner.Name);
        Assert.Equal(7, restored.Inner.Age);
    }

    [Fact]
    public void GeneratedCodec_IsDiscoverable_ViaAssemblyScanning()
    {
        var serializer = CreateSerializer();
        var codec = serializer.GetCodec<SimpleRecord>();
        Assert.NotNull(codec);
    }

    [Fact]
    public void GeneratedCodec_SupportsDynamicSerialization()
    {
        var serializer = CreateSerializer();
        var original = new SimpleRecord("dynamic-test", 99);

        var writer = new BinaryBufferWriter();
        serializer.WriteDynamic(writer, original);
        var reader = new BinaryBufferReader(writer.WrittenSpan);
        var restored = Assert.IsType<SimpleRecord>(serializer.ReadDynamic(ref reader));

        Assert.Equal("dynamic-test", restored.Name);
        Assert.Equal(99, restored.Age);
    }

    [Fact]
    public void PolymorphicInterface_RoundTrips_Circle()
    {
        var serializer = CreateSerializer();
        var original = new ShapeContainer("my-circle", new Circle(5.0));

        var restored = serializer.Deserialize<ShapeContainer>(serializer.Serialize(original));

        Assert.Equal("my-circle", restored.Label);
        var circle = Assert.IsType<Circle>(restored.Shape);
        Assert.Equal(5.0, circle.Radius);
    }

    [Fact]
    public void PolymorphicInterface_RoundTrips_Rectangle()
    {
        var serializer = CreateSerializer();
        var original = new ShapeContainer("my-rect", new Rectangle(3.0, 4.0));

        var restored = serializer.Deserialize<ShapeContainer>(serializer.Serialize(original));

        Assert.Equal("my-rect", restored.Label);
        var rect = Assert.IsType<Rectangle>(restored.Shape);
        Assert.Equal(3.0, rect.Width);
        Assert.Equal(4.0, rect.Height);
    }

    [Fact]
    public void PolymorphicInterface_RoundTrips_WithNullableSecondary()
    {
        var serializer = CreateSerializer();
        var original = new MultiShapeContainer("mixed", new Circle(1.0), null);

        var restored = serializer.Deserialize<MultiShapeContainer>(serializer.Serialize(original));

        Assert.Equal("mixed", restored.Label);
        Assert.IsType<Circle>(restored.Primary);
        Assert.Null(restored.Secondary);
    }

    [Fact]
    public void PolymorphicInterface_RoundTrips_WithBothShapes()
    {
        var serializer = CreateSerializer();
        var original = new MultiShapeContainer("both", new Circle(2.0), new Rectangle(5.0, 6.0));

        var restored = serializer.Deserialize<MultiShapeContainer>(serializer.Serialize(original));

        Assert.IsType<Circle>(restored.Primary);
        var secondary = Assert.IsType<Rectangle>(restored.Secondary);
        Assert.Equal(5.0, secondary.Width);
    }

    [Fact]
    public void PolymorphicAbstractClass_RoundTrips_Dog()
    {
        var serializer = CreateSerializer();
        var original = new PetOwner("Alice", new Dog { Name = "Rex" });

        var restored = serializer.Deserialize<PetOwner>(serializer.Serialize(original));

        Assert.Equal("Alice", restored.OwnerName);
        var dog = Assert.IsType<Dog>(restored.Pet);
        Assert.Equal("Rex", dog.Name);
    }

    [Fact]
    public void PolymorphicAbstractClass_RoundTrips_Cat()
    {
        var serializer = CreateSerializer();
        var original = new PetOwner("Bob", new Cat { Name = "Whiskers" });

        var restored = serializer.Deserialize<PetOwner>(serializer.Serialize(original));

        Assert.Equal("Bob", restored.OwnerName);
        var cat = Assert.IsType<Cat>(restored.Pet);
        Assert.Equal("Whiskers", cat.Name);
    }

    [Fact]
    public void TypeManifest_TracksKnownSubtypes()
    {
        var serializer = CreateSerializer();
        var manifest = TypeManifest.Build(serializer);

        var shapeSubtypes = manifest.GetKnownSubtypes(typeof(IShape));
        Assert.Contains(shapeSubtypes, e => e.ConcreteType == typeof(Circle));
        Assert.Contains(shapeSubtypes, e => e.ConcreteType == typeof(Rectangle));

        var animalSubtypes = manifest.GetKnownSubtypes(typeof(Animal));
        Assert.Contains(animalSubtypes, e => e.ConcreteType == typeof(Dog));
        Assert.Contains(animalSubtypes, e => e.ConcreteType == typeof(Cat));
    }

    [Fact]
    public void TypeManifest_ResolvesByAlias()
    {
        var serializer = CreateSerializer();
        var manifest = TypeManifest.Build(serializer);

        var entry = manifest.GetEntry("generated.circle");
        Assert.NotNull(entry);
        Assert.Equal(typeof(Circle), entry.ConcreteType);
    }

    private static BinarySerializer CreateSerializer()
        => new BinarySerializerBuilder()
            .AddCodecsFromAssembly(typeof(SimpleRecord).Assembly)
            .Build();
}
