// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO.Pipelines;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

public partial class OpenApiSchemaServiceTests : OpenApiDocumentServiceTestBase
{
    [Fact]
    public async Task GetOpenApiRequestBody_GeneratesSchemaForPoco()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/", (Todo todo) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/"].Operations[HttpMethod.Post];
            var requestBody = operation.RequestBody;

            Assert.NotNull(requestBody);
            var content = Assert.Single(requestBody.Content);
            Assert.Equal("application/json", content.Key);
            Assert.NotNull(content.Value.Schema);
            var schema = content.Value.Schema;
            Assert.Equal(JsonSchemaType.Object, schema.Type);
            Assert.Collection(schema.Properties,
                property =>
                {
                    Assert.Equal("id", property.Key);
                    Assert.Equal(JsonSchemaType.Integer, property.Value.Type);
                },
                property =>
                {
                    Assert.Equal("title", property.Key);
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, property.Value.Type);
                },
                property =>
                {
                    Assert.Equal("completed", property.Key);
                    Assert.Equal(JsonSchemaType.Boolean, property.Value.Type);
                },
                property =>
                {
                    Assert.Equal("createdAt", property.Key);
                    Assert.Equal(JsonSchemaType.String, property.Value.Type);
                    Assert.Equal("date-time", property.Value.Format);
                });

        });
    }

    [Theory]
    [InlineData(false, "application/json")]
    [InlineData(true, "application/x-www-form-urlencoded")]
    [InlineData(true, "multipart/form-data")]
    public async Task GetOpenApiRequestBody_GeneratesSchemaForPoco_WithValidationAttributes(bool isFromForm, string targetContentType)
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        if (isFromForm)
        {
            builder.MapPost("/", ([FromForm] ProjectBoard todo) => { });
        }
        else
        {
            builder.MapPost("/", (ProjectBoard todo) => { });
        }

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/"].Operations[HttpMethod.Post];
            var requestBody = operation.RequestBody;

            Assert.NotNull(requestBody);
            var content = requestBody.Content[targetContentType];
            Assert.NotNull(content.Schema);
            var effectiveSchema = content.Schema;
            Assert.Equal(JsonSchemaType.Object, effectiveSchema.Type);
            Assert.Collection(effectiveSchema.Properties,
                property =>
                {
                    Assert.Equal("id", property.Key);
                    Assert.Equal(JsonSchemaType.Integer, property.Value.Type);
                    Assert.Equal("1", property.Value.Minimum);
                    Assert.Equal("100", property.Value.Maximum);
                    Assert.Null(property.Value.Default);
                },
                property =>
                {
                    Assert.Equal("name", property.Key);
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, property.Value.Type);
                    Assert.Equal(5, property.Value.MinLength);
                    Assert.Equal(10, property.Value.MaxLength);
                    Assert.Null(property.Value.Default);
                },
                property =>
                {
                    Assert.Equal("description", property.Key);
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, property.Value.Type);
                    Assert.Equal(5, property.Value.MinLength);
                    Assert.Equal(10, property.Value.MaxLength);
                },
                property =>
                {
                    Assert.Equal("isPrivate", property.Key);
                    Assert.Equal(JsonSchemaType.Boolean, property.Value.Type);
                    Assert.True(property.Value.Default.GetValue<bool>());
                },
                property =>
                {
                    Assert.Equal("items", property.Key);
                    Assert.Equal(JsonSchemaType.Array | JsonSchemaType.Null, property.Value.Type);
                    Assert.Equal(10, property.Value.MaxItems);
                },
                property =>
                {
                    Assert.Equal("tags", property.Key);
                    Assert.Equal(JsonSchemaType.Array | JsonSchemaType.Null, property.Value.Type);
                    Assert.Equal(5, property.Value.MinItems);
                    Assert.Equal(10, property.Value.MaxItems);
                });
        });
    }

    [Fact]
    public async Task GetOpenApiRequestBody_RespectsRequiredAttributeOnBodyParameter()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/required-poco", ([Required] Todo todo) => { });
        builder.MapPost("/non-required-poco", (Todo todo) => { });
        builder.MapPost("/required-form", ([Required][FromForm] Todo todo) => { });
        builder.MapPost("/non-required-form", ([FromForm] Todo todo) => { });
        builder.MapPost("/", (ProjectBoard todo) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            Assert.True(GetRequestBodyForPath(document, "/required-poco").Required);
            Assert.False(GetRequestBodyForPath(document, "/non-required-poco").Required);
            // Form bodies are always required for form-based requests Individual elements
            // within the form can be optional.
            Assert.True(GetRequestBodyForPath(document, "/required-form").Required);
            Assert.True(GetRequestBodyForPath(document, "/non-required-form").Required);
        });

        static OpenApiRequestBody GetRequestBodyForPath(OpenApiDocument document, string path)
        {
            var operation = document.Paths[path].Operations[HttpMethod.Post];
            return operation.RequestBody as OpenApiRequestBody;
        }
    }

    [Fact]
    public async Task GetOpenApiRequestBody_RespectsRequiredAttributeOnBodyProperties()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/required-properties", (RequiredTodo todo) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/required-properties"].Operations[HttpMethod.Post];
            var requestBody = operation.RequestBody;
            var content = Assert.Single(requestBody.Content);
            var schema = content.Value.Schema;
            Assert.Collection(schema.Required,
                property => Assert.Equal("title", property),
                property => Assert.Equal("completed", property));
            Assert.DoesNotContain("assignee", schema.Required);
        });
    }

    [Fact]
    public async Task GetOpenApiRequestBody_GeneratesSchemaForFileTypes()
    {
        // Arrange
        var builder = CreateBuilder();
        string[] paths = ["stream", "pipereader"];

        // Act
        builder.MapPost("/stream", ([FromBody] Stream stream) => { });
        builder.MapPost("/pipereader", ([FromBody] PipeReader stream) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            foreach (var path in paths)
            {
                var operation = document.Paths[$"/{path}"].Operations[HttpMethod.Post];
                var requestBody = operation.RequestBody;

                var effectiveSchema = requestBody.Content["application/octet-stream"].Schema;

                Assert.Equal(JsonSchemaType.String, effectiveSchema.Type);
                Assert.Equal("binary", effectiveSchema.Format);
            }
        });
    }

    [Fact]
    public async Task GetOpenApiRequestBody_GeneratesSchemaForFilesInRecursiveType()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/proposal", (Proposal stream) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths[$"/proposal"].Operations[HttpMethod.Post];
            var requestBody = operation.RequestBody;
            var schema = requestBody.Content["application/json"].Schema;
            Assert.Equal("Proposal", ((OpenApiSchemaReference)schema).Reference.Id);
            var effectiveSchema = schema;
            Assert.Collection(effectiveSchema.Properties,
                property => {
                    Assert.Equal("proposalElement", property.Key);
                    Assert.Equal("Proposal", ((OpenApiSchemaReference)property.Value).Reference.Id);
                },
                property => {
                    Assert.Equal("stream", property.Key);
                    var targetSchema = property.Value;
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, targetSchema.Type);
                    Assert.Equal("binary", targetSchema.Format);
                });
        });
    }

    [Fact]
    public async Task GetOpenApiRequestBody_GeneratesSchemaForListOf()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/enumerable-todo", (IEnumerable<Todo> todo) => { });
        builder.MapPost("/array-todo", (Todo[] todo) => { });
        builder.MapGet("/array-parsable", (Guid[] guids) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var enumerableTodo = document.Paths["/enumerable-todo"].Operations[HttpMethod.Post];
            var arrayTodo = document.Paths["/array-todo"].Operations[HttpMethod.Post];
            var arrayParsable = document.Paths["/array-parsable"].Operations[HttpMethod.Get];

            Assert.NotNull(enumerableTodo.RequestBody);
            Assert.NotNull(arrayTodo.RequestBody);
            var parameter = Assert.Single(arrayParsable.Parameters);

            var enumerableTodoSchema = enumerableTodo.RequestBody.Content["application/json"].Schema;
            var arrayTodoSchema = arrayTodo.RequestBody.Content["application/json"].Schema;
            // Assert that both IEnumerable<Todo> and Todo[] have items that map to the same schema
            Assert.Equal(((OpenApiSchemaReference)enumerableTodoSchema.Items).Reference.Id, ((OpenApiSchemaReference)arrayTodoSchema.Items).Reference.Id);
            // Assert all types materialize as arrays
            Assert.Equal(JsonSchemaType.Array, enumerableTodoSchema.Type);
            Assert.Equal(JsonSchemaType.Array, arrayTodoSchema.Type);

            Assert.Equal(JsonSchemaType.Array, parameter.Schema.Type);
            Assert.Equal(JsonSchemaType.String, parameter.Schema.Items.Type);
            Assert.Equal("uuid", parameter.Schema.Items.Format);

            // Assert the array items are the same as the Todo schema
            foreach (var element in new[] { enumerableTodoSchema, arrayTodoSchema })
            {
                Assert.Collection(element.Items.Properties,
                    property =>
                    {
                        Assert.Equal("id", property.Key);
                        Assert.Equal(JsonSchemaType.Integer, property.Value.Type);
                    },
                    property =>
                    {
                        Assert.Equal("title", property.Key);
                        Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, property.Value.Type);
                    },
                    property =>
                    {
                        Assert.Equal("completed", property.Key);
                        Assert.Equal(JsonSchemaType.Boolean, property.Value.Type);
                    },
                    property =>
                    {
                        Assert.Equal("createdAt", property.Key);
                        Assert.Equal(JsonSchemaType.String, property.Value.Type);
                        Assert.Equal("date-time", property.Value.Format);
                    });
            }
        });
    }

    [Fact]
    public async Task GetOpenApiRequestBody_HandlesPolymorphicRequestWithoutDiscriminator()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/api", (Boat boat) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/api"].Operations[HttpMethod.Post];
            Assert.NotNull(operation.RequestBody);
            var requestBody = operation.RequestBody.Content;
            Assert.True(requestBody.TryGetValue("application/json", out var mediaType));
            var schema = mediaType.Schema;
            Assert.Equal(JsonSchemaType.Object, schema.Type);
            Assert.Null(schema.AnyOf);
            Assert.Collection(schema.Properties,
                property =>
                {
                    Assert.Equal("length", property.Key);
                    Assert.Equal(JsonSchemaType.Number, property.Value.Type);
                    Assert.Equal("double", property.Value.Format);
                },
                property =>
                {
                    Assert.Equal("wheels", property.Key);
                    Assert.Equal(JsonSchemaType.Integer, property.Value.Type);
                    Assert.Equal("int32", property.Value.Format);
                },
                property =>
                {
                    Assert.Equal("make", property.Key);
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, property.Value.Type);
                });
        });
    }

    [Fact]
    public async Task GetOpenApiRequestBody_HandlesDescriptionAttributeOnProperties()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/api", (DescriptionTodo todo) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/api"].Operations[HttpMethod.Post];
            Assert.NotNull(operation.RequestBody);
            var requestBody = operation.RequestBody.Content;
            Assert.True(requestBody.TryGetValue("application/json", out var mediaType));
            var schema = mediaType.Schema;
            Assert.Equal(JsonSchemaType.Object, schema.Type);
            Assert.Collection(schema.Properties,
                property =>
                {
                    Assert.Equal("id", property.Key);
                    Assert.Equal(JsonSchemaType.Integer, property.Value.Type);
                    Assert.Equal("int32", property.Value.Format);
                    Assert.Equal("The unique identifier for a todo item.", property.Value.Description);
                },
                property =>
                {
                    Assert.Equal("title", property.Key);
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, property.Value.Type);
                    Assert.Equal("The title of the todo item.", property.Value.Description);
                },
                property =>
                {
                    Assert.Equal("completed", property.Key);
                    Assert.Equal(JsonSchemaType.Boolean, property.Value.Type);
                    Assert.Equal("The completion status of the todo item.", property.Value.Description);
                },
                property =>
                {
                    Assert.Equal("createdAt", property.Key);
                    Assert.Equal(JsonSchemaType.String, property.Value.Type);
                    Assert.Equal("date-time", property.Value.Format);
                    Assert.Equal("The date and time the todo item was created.", property.Value.Description);
                });
        });
    }

    [Fact]
    public async Task GetOpenApiRequestBody_HandlesDescriptionAttributeOnParameter()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/api", ([Description("The todo item to create.")] DescriptionTodo todo) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/api"].Operations[HttpMethod.Post];
            Assert.NotNull(operation.RequestBody);
            Assert.Equal("The todo item to create.", operation.RequestBody.Description);
        });
    }

    [Fact]
    public async Task GetOpenApiRequestBody_HandlesNullableProperties()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/api", (NullablePropertiesType type) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/api"].Operations[HttpMethod.Post];
            var requestBody = operation.RequestBody;
            var content = Assert.Single(requestBody.Content);
            var schema = content.Value.Schema;
            Assert.Collection(schema.Properties,
                property =>
                {
                    Assert.Equal("nullableInt", property.Key);
                    Assert.Equal(JsonSchemaType.Integer | JsonSchemaType.Null, property.Value.Type);
                },
                property =>
                {
                    Assert.Equal("nullableString", property.Key);
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null | JsonSchemaType.Null, property.Value.Type);
                },
                property =>
                {
                    Assert.Equal("nullableBool", property.Key);
                    Assert.Equal(JsonSchemaType.Boolean | JsonSchemaType.Null, property.Value.Type);
                },
                property =>
                {
                    Assert.Equal("nullableDateTime", property.Key);
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null | JsonSchemaType.Null, property.Value.Type);
                    Assert.Equal("date-time", property.Value.Format);
                },
                property =>
                {
                    Assert.Equal("nullableUri", property.Key);
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null | JsonSchemaType.Null, property.Value.Type);
                    Assert.Equal("uri", property.Value.Format);
                });
        });
    }

    [Fact]
    public async Task SupportsNestedTypes()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/api", (NestedType type) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/api"].Operations[HttpMethod.Post];
            var requestBody = operation.RequestBody;
            var content = Assert.Single(requestBody.Content);
            Assert.Equal("NestedType", ((OpenApiSchemaReference)content.Value.Schema).Reference.Id);
            var schema = content.Value.Schema;
            Assert.Collection(schema.Properties,
                property =>
                {
                    Assert.Equal("name", property.Key);
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, property.Value.Type);
                },
                property =>
                {
                    Assert.Equal("nested", property.Key);
                    Assert.Equal("NestedType", ((OpenApiSchemaReference)property.Value).Reference.Id);
                });
        });
    }

    [Fact]
    public async Task SupportsNestedTypes_WithNoAttributeProvider()
    {
        // Arrange: this test ensures that we can correctly handle the scenario
        // where the attribute provider is null and we need to patch the property mappings
        // that are created by the underlying JsonSchemaExporter.
        var serviceCollection = new ServiceCollection();
        serviceCollection.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolver = options.SerializerOptions.TypeInfoResolver?.WithAddedModifier(jsonTypeInfo =>
            {
                foreach (var propertyInfo in jsonTypeInfo.Properties)
                {
                    propertyInfo.AttributeProvider = null;
                }

            });
        });
        var builder = CreateBuilder(serviceCollection);

        // Act
        builder.MapPost("/api", (NestedType type) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/api"].Operations[HttpMethod.Post];
            var requestBody = operation.RequestBody;
            var content = Assert.Single(requestBody.Content);
            Assert.Equal("NestedType", ((OpenApiSchemaReference)content.Value.Schema).Reference.Id);
            var schema = content.Value.Schema;
            Assert.Collection(schema.Properties,
                property =>
                {
                    Assert.Equal("name", property.Key);
                    Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, property.Value.Type);
                },
                property =>
                {
                    Assert.Equal("nested", property.Key);
                    Assert.Equal("NestedType", ((OpenApiSchemaReference)property.Value).Reference.Id);
                });
        });
    }

    private class DescriptionTodo
    {
        [Description("The unique identifier for a todo item.")]
        public int Id { get; set; }

        [Description("The title of the todo item.")]
        public string Title { get; set; }

        [Description("The completion status of the todo item.")]
        public bool Completed { get; set; }

        [Description("The date and time the todo item was created.")]
        public DateTime CreatedAt { get; set; }
    }

#nullable enable
    private class NullablePropertiesType
    {
        public int? NullableInt { get; set; }
        public string? NullableString { get; set; }
        public bool? NullableBool { get; set; }
        public DateTime? NullableDateTime { get; set; }
        public Uri? NullableUri { get; set; }
    }
#nullable restore

    private class NestedType
    {
        public string Name { get; set; }
        public NestedType Nested { get; set; }
    }

    [Fact]
    public async Task ExcludesNullabilityInFormParameters()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
#nullable enable
        builder.MapPost("/api", ([FromForm] string? name, [FromForm] int? number, [FromForm] int[]? ids) => { });
#nullable restore

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/api"].Operations[HttpMethod.Post];
            Assert.Collection(operation.RequestBody.Content["application/x-www-form-urlencoded"].Schema.AllOf,
                schema =>
                {
                    var property = schema.Properties["name"];
                    Assert.Equal(JsonSchemaType.String, property.Type);
                },
                schema =>
                {
                    var property = schema.Properties["number"];
                    Assert.Equal(JsonSchemaType.Integer, property.Type);
                },
                schema =>
                {
                    var property = schema.Properties["ids"];
                    Assert.Equal(JsonSchemaType.Array, property.Type);
                });
        });
    }

    [Fact]
    public async Task SupportsClassWithJsonUnmappedMemberHandlingDisallowed()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/api", (ExampleWithDisallowedUnmappedMembers type) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/api"].Operations[HttpMethod.Post];
            var requestBody = operation.RequestBody;
            var content = Assert.Single(requestBody.Content);
            var schema = content.Value.Schema;
            Assert.Collection(schema.Properties,
                property =>
                {
                    Assert.Equal("number", property.Key);
                    Assert.Equal(JsonSchemaType.Integer, property.Value.Type);
                });
            Assert.False(schema.AdditionalPropertiesAllowed);
        });
    }

    [Fact]
    public async Task SupportsClassWithJsonUnmappedMemberHandlingSkipped()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/api", (ExampleWithSkippedUnmappedMembers type) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/api"].Operations[HttpMethod.Post];
            var requestBody = operation.RequestBody;
            var content = Assert.Single(requestBody.Content);
            var schema = content.Value.Schema;
            Assert.Collection(schema.Properties,
                property =>
                {
                    Assert.Equal("number", property.Key);
                    Assert.Equal(JsonSchemaType.Integer, property.Value.Type);
                });
            Assert.True(schema.AdditionalPropertiesAllowed);
        });
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private class ExampleWithDisallowedUnmappedMembers
    {
        public int Number { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
    private class ExampleWithSkippedUnmappedMembers
    {
        public int Number { get; init; }
    }

    [Fact]
    public async Task SupportsTypesWithSelfReferencedProperties()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/api", (Parent parent) => { });

        // Assert
        await VerifyOpenApiDocument(builder, document =>
        {
            var operation = document.Paths["/api"].Operations[HttpMethod.Post];
            var requestBody = operation.RequestBody;
            var content = Assert.Single(requestBody.Content);
            var schema = content.Value.Schema;
            Assert.Collection(schema.Properties,
                property =>
                {
                    Assert.Equal("selfReferenceList", property.Key);
                    Assert.Equal(JsonSchemaType.Null | JsonSchemaType.Array, property.Value.Type);
                    Assert.Equal("Parent", ((OpenApiSchemaReference)property.Value.Items).Reference.Id);
                },
                property =>
                {
                    Assert.Equal("selfReferenceDictionary", property.Key);
                    Assert.Equal(JsonSchemaType.Null | JsonSchemaType.Object, property.Value.Type);
                    Assert.Equal("Parent", ((OpenApiSchemaReference)property.Value.AdditionalProperties).Reference.Id);
                });
        });
    }

    public class Parent
    {
        public IEnumerable<Parent> SelfReferenceList { get; set; } = [];
        public IDictionary<string, Parent> SelfReferenceDictionary { get; set; } = new Dictionary<string, Parent>();
    }

    /// <remarks>
    /// Regression test for https://github.com/dotnet/aspnetcore/issues/61327
    /// </remarks>
    [Fact]
    public async Task RespectsEnumDefaultValueInControllerFormParameters()
    {
        // Arrange
        var actionDescriptor = CreateActionDescriptor(nameof(TestBodyController.FormPostWithOptionalEnumParam), typeof(TestBodyController));

        // Assert
        await VerifyOpenApiDocument(actionDescriptor, VerifyOptionalEnum);
    }

    [Fact]
    public async Task RespectsEnumDefaultValueInMinimalApiFormParameters()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.MapPost("/optionalEnum", ([FromForm(Name = "status")] Status status = Status.Approved) => { });

        // Assert
        await VerifyOpenApiDocument(builder, VerifyOptionalEnum);
    }

    private void VerifyOptionalEnum(OpenApiDocument document)
    {
        var operation = document.Paths["/optionalEnum"].Operations[HttpMethod.Post];
        var properties = operation.RequestBody.Content["application/x-www-form-urlencoded"].Schema.Properties;
        var property = properties["status"];

        Assert.NotNull(property);
        Assert.Equal(3, property.Enum.Count);
        Assert.Equal("Approved", property.Default.GetValue<string>());
    }

    [ApiController]
    [Produces("application/json")]
    public class TestBodyController
    {
        [Route("/optionalEnum")]
        [HttpPost]
        internal Status FormPostWithOptionalEnumParam(
            [FromForm(Name = "status")] Status status = Status.Approved
        ) => status;
    }
}
