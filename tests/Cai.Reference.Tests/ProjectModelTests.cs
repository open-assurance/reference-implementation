using Cai.Reference.Engine;
using System.Xml.Linq;

namespace Cai.Reference.Tests;

public class ProjectModelTests
{
    [Theory]
    [InlineData("$(MSBuildProjectName.Contains('.Tests')) AND '$(MSBuildProjectName)' != 'Acme.Tests.Common'", "Acme.Orders.Tests", true)]
    [InlineData("$(MSBuildProjectName.Contains('.Tests')) AND '$(MSBuildProjectName)' != 'Acme.Tests.Common'", "Acme.Tests.Common", false)]
    [InlineData("$(MSBuildProjectName.Contains('.Tests'))", "Acme.Orders", false)]
    [InlineData("'$(Configuration)' == 'Release'", "Acme.Orders", false)]            // an undefined property is empty, as in MSBuild
    [InlineData("'$(Configuration)' != 'Release'", "Acme.Orders", true)]
    [InlineData("!$(MSBuildProjectName.EndsWith('.Tests')) or '$(MSBuildProjectName)' == 'X'", "Acme.Orders", true)]
    [InlineData("Exists('$(MSBuildThisFileDirectory)x.props')", "Acme.Orders", false)]   // unparseable → does not hold
    public void MsBuild_conditions_scope_inherited_groups_to_the_right_projects(string condition, string project, bool expected) =>
        Assert.Equal(expected, MsBuildCondition.Evaluate(condition, project));

    [Fact]
    public void A_shared_props_file_that_turns_only_dot_Tests_projects_into_test_projects_does_not_make_everything_a_test()
    {
        using var fx = new Fixture()
            .Add("Directory.Build.props", """
                <Project>
                  <PropertyGroup Condition="$(MSBuildProjectName.Contains('.Tests'))"><IsTestProject>true</IsTestProject></PropertyGroup>
                  <ItemGroup Condition="$(MSBuildProjectName.Contains('.Tests'))"><PackageReference Include="xunit.v3" Version="1.0.0" /></ItemGroup>
                </Project>
                """)
            .Project("Acme.Orders", sources: ("Order.cs", "public sealed class Order { public int Id { get; init; } public decimal Total(int q, decimal p) => q * p; }"))
            .Project("Acme.Orders.Tests", sources: ("OrderTests.cs", "public class OrderTests { [Xunit.Fact] public void T() { Xunit.Assert.Equal(2m, new Order().Total(1, 2m)); } }"));
        fx.Run(_ => { });   // materialise the files
        var repo = Repository.Open(fx.Root);
        Assert.Equal(ProjectRole.Library, repo.Projects.Single(p => p.Name == "Acme.Orders").Role);
        Assert.Equal(ProjectRole.Test, repo.Projects.Single(p => p.Name == "Acme.Orders.Tests").Role);
    }

    [Fact]
    public void Compile_Remove_globs_keep_template_sources_out_of_the_project()
    {
        using var fx = new Fixture()
            .Project("Acme.Web", web: true, extraProps: null, sources: ("Program.cs", "var b = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args); b.Build().Run();"))
            .Add("src/Acme.Web/wwwroot/Templates/Thing/[Module]Controller.cs", "public class Broken {")
            .Add("src/Acme.Web/Acme.Web.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
                  <ItemGroup><Compile Remove="wwwroot\Templates\**" /></ItemGroup>
                </Project>
                """);
        fx.Run(_ => { });   // materialise the files
        var repo = Repository.Open(fx.Root);
        var web = repo.Projects.Single();
        Assert.Contains("src/Acme.Web/Program.cs", web.SourceFiles);
        Assert.DoesNotContain(web.SourceFiles, f => f.Contains("Templates"));
    }

    [Fact]
    public void A_runtime_project_with_a_database_driver_is_infrastructure_whatever_its_prefix_says()
    {
        using var fx = new Fixture()
            .Project("Acme.Kernel.Runtime", extraProps: null, sources: ("Store.cs", "public sealed class Store { }"))
            .Project("Acme.Kernel.Contracts", sources: ("IStore.cs", "public interface IStore { }"))
            .Project("Acme.Kernel", sources: ("Money.cs", "public readonly record struct Money(decimal Amount);"));
        fx.Run(_ => { });   // materialise the files
        var repo = Repository.Open(fx.Root);
        Assert.Equal(ProjectRole.Infrastructure, repo.Projects.Single(p => p.Name == "Acme.Kernel.Runtime").Role);
        Assert.Equal(ProjectRole.Library, repo.Projects.Single(p => p.Name == "Acme.Kernel.Contracts").Role);
        Assert.Equal(ProjectRole.Domain, repo.Projects.Single(p => p.Name == "Acme.Kernel").Role);
    }
}
