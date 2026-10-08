using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cai.Reference.Engine;

/// <summary>Domain Modelling (DM1–DM7, DM9–DM12), Event-Driven (ED1–ED4) and Event Sourcing (ES1, ES2), with the lens-applicability decisions.</summary>
public static class Domain
{
    private static readonly string[] DmDims = { "DM1", "DM2", "DM3", "DM4", "DM5", "DM6", "DM7", "DM9", "DM10", "DM11", "DM12" };
    private static readonly string[] EdDims = { "ED1", "ED2", "ED3", "ED4" };
    private static readonly string[] EsDims = { "ES1", "ES2" };

    private sealed class Model
    {
        public Dictionary<string, TypeDeclarationSyntax> Types { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (ProjectCompilation p, SyntaxTree tree)> EnumOwner { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (ProjectCompilation p, SyntaxTree tree)> Owner { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Roots { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Entities { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IdTypes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> DomainEvents { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IntegrationEvents { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Commands { get; } = new(StringComparer.Ordinal);
        public HashSet<string> DomainProjects { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SharedProjects { get; } = new(StringComparer.Ordinal);
        public bool HasFolds, HasBus, HasEventStore;
    }

    public static void Run(ScanContext ctx)
    {
        var m = Discover(ctx);
        var domainApplies = m.Roots.Count > 0 || m.Entities.Count > 0 || m.DomainProjects.Count > 0;
        var eventsApply = m.DomainEvents.Count > 0 || m.IntegrationEvents.Count > 0 || m.HasBus || m.Commands.Count > 0;
        var esApplies = m.HasFolds || m.HasEventStore;
        ctx.Facts["lensApplicability"] = $"domainModelling={domainApplies} eventDriven={eventsApply} eventSourcing={esApplies}";
        if (!domainApplies) foreach (var d in DmDims) ctx.Skip(d, "lens not applicable: no domain model detected (no aggregate/entity base types, no Domain project)");
        else DomainModel(ctx, m);
        if (!eventsApply) foreach (var d in EdDims) ctx.Skip(d, "lens not applicable: no messaging, events, commands or handlers detected");
        else Events(ctx, m);
        if (!esApplies) foreach (var d in EsDims) ctx.Skip(d, "lens not applicable: no event-sourced folds (Apply/When) or event store detected");
        else EventSourcing(ctx, m);
    }

    private static bool IsEntityLike(TypeDeclarationSyntax t) => t is ClassDeclarationSyntax && t.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text == "Id") && t.Members.OfType<MethodDeclarationSyntax>().Any();

    private static Model Discover(ScanContext ctx)
    {
        var m = new Model();
        foreach (var p in ctx.Repo.Projects.Where(p => p.IsProduction))
        {
            if (p.Role == ProjectRole.Domain) m.DomainProjects.Add(p.Path);
            if (Regex.IsMatch(p.Name, @"(?i)\.(Contracts|Shared|SharedKernel|Kernel|Common|Abstractions|Messages|IntegrationEvents|Messaging|BuildingBlocks)(\.|$)")) m.SharedProjects.Add(p.Path);
        }
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var root = tree.GetRoot();
            var text = root.ToString();
            if (Regex.IsMatch(text, @"\b(IPublishEndpoint|IBus\b|IMessageBus|IEventBus|IPublisher|IMediator|ISender|IEventPublisher|IMessageSession|IProducer<|ServiceBusSender|IModel\b.*BasicPublish|\.PublishAsync\(|\.Publish\(|\.SendAsync\()")) m.HasBus = true;
            if (Regex.IsMatch(text, @"\b(IEventStore|IDocumentSession|IEventStoreClient|EventStoreClient|AppendToStream|FetchStream|StartStream|IAggregateRepository|LoadFromHistory|ReplayEvents|IEventSourced)")) m.HasEventStore = true;
            foreach (var e in root.DescendantNodes().OfType<EnumDeclarationSyntax>()) m.EnumOwner.TryAdd(e.Identifier.Text, (p, tree));
            foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var name = t.Identifier.Text;
                if (!m.Types.ContainsKey(name)) { m.Types[name] = t; m.Owner[name] = (p, tree); }
                if (t is InterfaceDeclarationSyntax || t.Modifiers.Any(SyntaxKind.AbstractKeyword)) continue;   // contracts are not events, commands or entities
                var bases = t.BaseList?.Types.Select(b => Cs.Simple(b.Type)).ToList() ?? new List<string>();
                var inDomain = p.Project.Role == ProjectRole.Domain || Regex.IsMatch(Cs.EnclosingNamespace(t), @"\.Domain(\.|$)");
                if (bases.Any(b => b is "AggregateRoot" or "Aggregate" or "AggregateBase" or "EventSourcedAggregate")) { m.Roots.Add(name); m.Entities.Add(name); }
                else if (bases.Any(b => b is "Entity" or "EntityBase" or "DomainEntity")) m.Entities.Add(name);
                else if (inDomain && IsEntityLike(t) && !Regex.IsMatch(name, @"(Service|Factory|Specification|Policy|Repository|Handler|Builder|Calculator|Validator)$")) m.Entities.Add(name);
                if (t is RecordDeclarationSyntax or StructDeclarationSyntax && Regex.IsMatch(name, @"(Id|Identifier|Reference|Number|Key)$") && (t.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) || t is RecordDeclarationSyntax)) m.IdTypes.Add(name);
                if (bases.Any(b => b is "IDomainEvent" or "DomainEvent" or "IEvent" or "INotification" or "EventBase")) m.DomainEvents.Add(name);
                if (bases.Any(b => b is "IIntegrationEvent" or "IntegrationEvent" or "IMessage") || name.EndsWith("IntegrationEvent")) m.IntegrationEvents.Add(name);
                if (bases.Any(b => b is "ICommand" or "IRequest" or "Command") && (name.EndsWith("Command") || bases.Contains("ICommand")) || name.EndsWith("Command") && t is RecordDeclarationSyntax or ClassDeclarationSyntax && name.EndsWith("Command") && !name.EndsWith("Handler")) m.Commands.Add(name);
                if (t.Members.OfType<MethodDeclarationSyntax>().Any(md => md.Identifier.Text is "Apply" or "When" && md.ParameterList.Parameters.Count == 1 && (m.DomainEvents.Contains(Cs.Simple(md.ParameterList.Parameters[0].Type)) || Regex.IsMatch(Cs.Simple(md.ParameterList.Parameters[0].Type), @"(ed|Event)$")))) m.HasFolds = true;
            }
        }
        // a root is also any type a repository is declared for
        if (m.Roots.Count == 0)   // only when the codebase does not name its roots itself
            foreach (var t in m.Types.Values.OfType<InterfaceDeclarationSyntax>())
                foreach (var b in t.BaseList?.Types ?? default) if (b.Type is GenericNameSyntax g && g.Identifier.Text is "IRepository" or "IAggregateRepository") { var agg = Cs.Simple(g.TypeArgumentList.Arguments[0]); if (m.Entities.Contains(agg) && !ChildOfAnother(m, agg)) m.Roots.Add(agg); }
        return m;
    }

    /// <summary>An entity held in a collection by another entity is a child, not a root.</summary>
    private static bool ChildOfAnother(Model m, string entity) =>
        m.Types.Values.Any(t => t.Identifier.Text != entity && (m.Entities.Contains(t.Identifier.Text) || m.Roots.Contains(t.Identifier.Text)) && t.Members.Any(mem => Regex.IsMatch(mem.ToString().Split('\n')[0], $@"(List|Collection|IReadOnlyList|IReadOnlyCollection|IEnumerable|HashSet|ICollection)<{Regex.Escape(entity)}>")));

    // ---------------- Domain Modelling ----------------
    private static void DomainModel(ScanContext ctx, Model m)
    {
        var roots = m.Roots.Count > 0 ? m.Roots : m.Entities;
        // entity-centred dimensions need DDD building blocks the code names itself (root/entity base types or repositories); a
        // plain layered service with Id-bearing records is not a domain model and must not be read as a bad one
        var ddd = m.Roots.Count > 0 || m.Types.Values.OfType<InterfaceDeclarationSyntax>().Any(i => i.Identifier.Text.EndsWith("Repository")) || m.Types.Values.Any(t => t.BaseList?.Types.Any(b => Cs.Simple(b.Type) is "Entity" or "EntityBase" or "ValueObject" or "AggregateRoot") == true);
        if (!ddd)
        {
            foreach (var d in new[] { "DM1", "DM2", "DM4", "DM5", "DM7", "DM9", "DM10", "DM11" }) ctx.Skip(d, "no DDD building blocks (aggregate/entity base types or repositories): the model's shape is not what this codebase claims");
            m.Entities.Clear();
        }
        var hasRehydrationFramework = ctx.Repo.Projects.Any(p => p.HasPackage("Marten") || p.HasPackage("Microsoft.EntityFrameworkCore"));
        int idProps = 0, typedIdProps = 0, entities = 0, anemic = 0;
        foreach (var name in m.Entities.OrderBy(x => x, StringComparer.Ordinal))
        {
            var t = m.Types[name]; var (p, tree) = m.Owner[name]; var rel = ctx.Workspace.RelPath(tree.FilePath); var model = p.Model(tree);
            entities++;
            var isReadModel = Regex.IsMatch(rel, @"(?i)/(Projections?|ReadModels?|Views?|Dtos?|Queries)/") || Regex.IsMatch(name, @"(View|ReadModel|Dto|Projection)$");
            if (isReadModel) continue;
            // DM1: a field/property typed as another aggregate ROOT (children and ids are fine)
            foreach (var member in t.Members)
            {
                var type = member switch { PropertyDeclarationSyntax pd => pd.Type, FieldDeclarationSyntax fd => fd.Declaration.Type, _ => null };
                if (type is null) continue;
                var simple = Cs.Simple(type);
                if (simple != name && roots.Contains(simple) && !(type is GenericNameSyntax))
                    ctx.Add(new Finding("cross-aggregate-object-reference", "DM1", $"{name} holds {simple} by object reference ({MemberName(member)}); an aggregate references another aggregate by its id", rel, Cs.Line(member), Cs.Line(member), 2));
            }
            // DM2: raw-primitive identifiers where the domain has typed ids
            foreach (var pd in t.Members.OfType<PropertyDeclarationSyntax>().Where(pd => pd.Identifier.Text.EndsWith("Id")))
            {
                idProps++;
                var simple = Cs.Simple(pd.Type);
                var stem = pd.Identifier.Text == "Id" ? name : pd.Identifier.Text[..^2];
                var ownConcept = pd.Identifier.Text == "Id" || m.Entities.Any(e => e.EndsWith(stem, StringComparison.Ordinal)) || m.IdTypes.Any(t => t.EndsWith(stem + "Id", StringComparison.Ordinal));   // `Guid OrderId` in a service that has no Order is a reference into another service, carried as that service publishes it
                if (simple is "Guid" or "int" or "long" or "string" or "Int32" or "Int64" or "String") { if (m.IdTypes.Count > 0 && ownConcept) ctx.Add(new Finding("primitive-entity-identifier", "DM2", $"{name}.{pd.Identifier.Text} is a raw {simple} while the domain has strongly-typed ids ({string.Join(", ", m.IdTypes.Take(3))}…): any id can be passed where this one is meant", rel, Cs.Line(pd), null, 1)); }
                else typedIdProps++;
            }
            // DM5: public setters on entity state
            var publicSetters = t.Members.OfType<PropertyDeclarationSyntax>().Where(pd => pd.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) && a.Modifiers.Count == 0) == true).ToList();
            if (publicSetters.Count > 0)
            {
                var loc = ctx.Loc(t);
                ctx.Add(new Finding("publicly-mutable-entity-state", "DM5", $"{name} exposes {publicSetters.Count} public setter(s) ({string.Join(", ", publicSetters.Select(x => x.Identifier.Text).Take(4))}): state can be changed past the aggregate's own methods", loc.File, loc.Line, loc.EndLine, hasRehydrationFramework ? 0.5 * publicSetters.Count : 1.0 * publicSetters.Count));
            }
            // DM4: state and no behaviour, while a service drives it
            var behaviour = t.Members.OfType<MethodDeclarationSyntax>().Where(md => md.Modifiers.Any(SyntaxKind.PublicKeyword) && md.Identifier.Text is not ("ToString" or "Equals" or "GetHashCode" or "Apply" or "When") && !md.Modifiers.Any(SyntaxKind.StaticKeyword)).ToList();
            var props = t.Members.OfType<PropertyDeclarationSyntax>().Count();
            var validatingFactories = t.Members.OfType<MethodDeclarationSyntax>().Count(md => md.Modifiers.Any(SyntaxKind.StaticKeyword) && Cs.Simple(md.ReturnType) == name && Regex.IsMatch(md.Body?.ToString() ?? md.ExpressionBody?.ToString() ?? "", @"throw|ThrowIf|Guard|Validate"));
            var computed = t.Members.OfType<PropertyDeclarationSyntax>().Count(pd => pd.ExpressionBody is not null || pd.AccessorList?.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null) == true);
            if (behaviour.Count == 0 && validatingFactories == 0 && computed == 0 && props >= 3)
            {
                var driver = m.Types.Keys.FirstOrDefault(k => k != name && Regex.IsMatch(k, $@"^{Regex.Escape(name)}(Service|Manager|Logic|Handler)s?$")) ?? m.Types.Keys.FirstOrDefault(k => k.EndsWith("Service") && m.Types[k].ToString().Contains(name + " ") );
                anemic++;
                var loc = ctx.Loc(t);
                ctx.Add(new Finding("anemic-domain-model", "DM4", $"{name} is {props} properties and no behaviour{(driver is not null ? $"; {driver} decides its rules from outside" : "")}: a data bag, not an aggregate", loc.File, loc.Line, loc.Line, 1));
            }
            // DM11: a public constructor that stores raw primitives unvalidated, with no factory beside it
            var factory = t.Members.OfType<MethodDeclarationSyntax>().Any(md => md.Modifiers.Any(SyntaxKind.StaticKeyword) && Cs.Simple(md.ReturnType) == name);
            foreach (var ctor in t.Members.OfType<ConstructorDeclarationSyntax>().Where(c => c.Modifiers.Any(SyntaxKind.PublicKeyword)))
            {
                var primitives = ctor.ParameterList.Parameters.Where(pp => Cs.Simple(pp.Type) is "string" or "int" or "decimal" or "double" or "long" or "float" or "DateTime" or "DateTimeOffset").ToList();
                if (primitives.Count == 0 || factory) continue;
                var body = ctor.Body?.ToString() ?? ctor.ExpressionBody?.ToString() ?? "";
                var validates = Regex.IsMatch(body, @"ThrowIf|Guard\.|throw new|Ensure|Validate|Require|if \(") || ctor.Initializer?.ArgumentList.Arguments.Any(a => a.ToString().Contains("Validate") || a.ToString().Contains("Guard")) == true;
                var stored = primitives.Any(pp => Regex.IsMatch(body, $@"=\s*{Regex.Escape(pp.Identifier.Text)}\s*;"));
                if (stored && !validates)
                    ctx.Add(new Finding("constructible-invalid-entity", "DM11", $"{name} can be constructed in a state its rules forbid: the public constructor stores {string.Join(", ", primitives.Select(pp => pp.Identifier.Text))} unchecked and no factory stands beside it", rel, Cs.Line(ctor), null, 1));
            }
            // DM12: ambient time / randomness inside the domain (identity generation is not a rule input)
            foreach (var ma in t.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (Cs.EnclosingMethod(ma) is MethodDeclarationSyntax fold && fold.Identifier.Text is "Apply" or "When" or "On") continue;   // a fold reading the clock is ES1's finding
                var txt = ma.ToString();
                if (Regex.IsMatch(txt, @"^(DateTime|DateTimeOffset)\.(Now|UtcNow|Today)$") || Regex.IsMatch(txt, @"^TimeProvider\.System\.GetUtcNow$") || Regex.IsMatch(txt, @"^Random\.Shared\.") || txt == "Environment.TickCount")
                    ctx.Add(new Finding("ambient-nondeterminism-in-domain", "DM12", $"{name}.{Cs.NameOf(Cs.EnclosingMethod(ma) ?? t)} reads {txt}: the rule it feeds cannot be tested at a chosen instant", rel, Cs.Line(ma), null, 1));
            }
            foreach (var oc in t.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(o => Cs.Simple(o.Type) == "Random"))
                ctx.Add(new Finding("ambient-nondeterminism-in-domain", "DM12", $"{name} creates its own Random", rel, Cs.Line(oc), null, 1));
        }
        // value objects: domain classes without identity must be immutable with value equality
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees.Where(x => x.project.Project.Role == ProjectRole.Domain || m.DomainProjects.Contains(x.project.Project.Path)))
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var name = cls.Identifier.Text;
                if (m.Entities.Contains(name) || m.DomainEvents.Contains(name) || cls.Modifiers.Any(SyntaxKind.StaticKeyword) || cls.Modifiers.Any(SyntaxKind.AbstractKeyword)) continue;
                if (!ddd) continue;
                if (Regex.IsMatch(name, @"(Service|Factory|Specification|Policy|Repository|Handler|Builder|Calculator|Validator|Exception|Options|Settings|Context|Store|Provider|Mapper|Visitor|Strategy|Rule|Evaluator|Resolver|Registry|Catalog|Manager|Client|Writer|Reader|Projection|View|Map|Table|Lookup|Index|Cache|Config|Result|Response|Request|Dto|Command|Query|Event|Message|Notification|Job|Worker|Middleware|Filter|Attribute|Converter|Serializer|Formatter|Parser|Generator|Helper|Extensions|Utils?)$")) continue;
                var props = cls.Members.OfType<PropertyDeclarationSyntax>().ToList();
                if (props.Count < 2 || props.Count > 8 || cls.Members.OfType<PropertyDeclarationSyntax>().Any(pd => pd.Identifier.Text == "Id")) continue;
                if (cls.Members.OfType<MethodDeclarationSyntax>().Count(md => md.Modifiers.Any(SyntaxKind.PublicKeyword) && md.Identifier.Text is not ("ToString" or "Equals" or "GetHashCode")) > 2) continue;
                var identityLike = props.Any(pd => Regex.IsMatch(pd.Identifier.Text, @"^(\w*Id|\w*Number|\w*Key|Sequence\w*)$") && pd.AccessorList?.Accessors.All(a => !a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.Modifiers.Count > 0) == true);
                var factoryShaped = cls.Members.OfType<ConstructorDeclarationSyntax>().Any() && cls.Members.OfType<ConstructorDeclarationSyntax>().All(c => !c.Modifiers.Any(SyntaxKind.PublicKeyword)) && cls.Members.OfType<MethodDeclarationSyntax>().Any(md => md.Modifiers.Any(SyntaxKind.StaticKeyword) && Cs.Simple(md.ReturnType) == name);
                if (identityLike || factoryShaped)
                {
                    // an identity, or a private constructor behind factories, makes this an entity: the question is whether its state is sealed
                    var open = props.Where(pd => pd.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) && a.Modifiers.Count == 0) == true).ToList();
                    if (open.Count > 0) ctx.Add(new Finding("publicly-mutable-entity-state", "DM5", $"{name} exposes {open.Count} public setter(s) ({string.Join(", ", open.Select(x => x.Identifier.Text).Take(4))}) beside its factory: state can be changed past the aggregate's own methods", rel, Cs.Line(open[0]), Cs.Line(open[^1]), open.Count));
                    continue;
                }
                var publicSetters = props.Count(pd => pd.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) && a.Modifiers.Count == 0) == true);
                var valueEquality = cls.Members.OfType<MethodDeclarationSyntax>().Any(md => md.Identifier.Text == "Equals") || cls.BaseList?.Types.Any(b => Cs.Simple(b.Type) is "ValueObject" or "IEquatable") == true;
                if (publicSetters > 0 || !valueEquality)
                {
                    var loc = ctx.Loc(cls);
                    ctx.Add(new Finding("value-object-mutability", "DM5", $"{name} models a value ({props.Count} properties, no identity) but is {(publicSetters > 0 ? "mutable through public setters" : "compared by reference")}: make it a record or give it value equality", loc.File, loc.Line, loc.EndLine, 1));
                }
            }
        }
        // DM3: integration events exposing producer domain types
        foreach (var evName in m.IntegrationEvents.OrderBy(x => x, StringComparer.Ordinal))
        {
            var t = m.Types[evName]; var (p, tree) = m.Owner[evName]; var rel = ctx.Workspace.RelPath(tree.FilePath);
            var types = (t is RecordDeclarationSyntax r && r.ParameterList is not null ? r.ParameterList.Parameters.Select(pp => (pp.Type, (SyntaxNode)pp)) : Enumerable.Empty<(TypeSyntax?, SyntaxNode)>()).Concat(t.Members.OfType<PropertyDeclarationSyntax>().Select(pd => (pd.Type, (SyntaxNode)pd)));
            foreach (var (type, node) in types)
            {
                var simple = Cs.Simple(type);
                if (!m.Owner.TryGetValue(simple, out var owner) && !m.EnumOwner.TryGetValue(simple, out owner)) continue;
                var ownerProject = owner.p.Project;
                if (m.SharedProjects.Contains(ownerProject.Path) || ownerProject.Path == p.Project.Path) continue;
                if (ownerProject.Role == ProjectRole.Domain || m.DomainProjects.Contains(ownerProject.Path))
                    ctx.Add(new Finding("integration-event-leaks-domain-type", "DM3", $"{evName} carries {simple}, a type of the producer's domain ({ownerProject.Name}); every consumer now compiles against that domain model", rel, Cs.Line(node), null, 1));
            }
        }
        // DM6: domain code depending on infrastructure
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees.Where(x => x.project.Project.Role == ProjectRole.Domain || m.DomainProjects.Contains(x.project.Project.Path)))
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath); var root = tree.GetRoot();
            foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>().Where(a => Cs.Simple(a.Name) is "Index" or "Table" or "Column" or "ForeignKey" or "Owned" or "Keyless" or "BsonElement" or "BsonId" or "JsonPropertyName" or "DataMember"))
                ctx.Add(new Finding("domain-depends-on-infrastructure", "DM6", $"[{Cs.Simple(attr.Name)}] on a domain type: persistence/serialisation mapping expressed inside the domain", rel, Cs.Line(attr), null, 1));
            foreach (var prm in root.DescendantNodes().OfType<ParameterSyntax>().Where(pp => Regex.IsMatch(Cs.Simple(pp.Type), @"^(DbContext|\w+DbContext|IDocumentSession|IDbConnection|HttpClient|IQuerySession|DbSet|IQueryable)$")))
                ctx.Add(new Finding("domain-depends-on-infrastructure", "DM6", $"domain code takes {Cs.Simple(prm.Type)} and reaches the database or network directly instead of through a port it owns", rel, Cs.Line(prm), null, 1));
            // first-party infrastructure: a domain type whose signature uses a type declared in an Infrastructure/Web project
            var graph = ctx.Graph;
            foreach (var dep in graph.References.GetValueOrDefault(rel) ?? new HashSet<string>())
            {
                var depRole = ctx.Repo.Projects.FirstOrDefault(x => x.Path == graph.FileProject.GetValueOrDefault(dep))?.Role;
                if (depRole is not (ProjectRole.Infrastructure or ProjectRole.Web or ProjectRole.Worker)) continue;
                var types = graph.DeclaredTypes[dep];
                var site = root.DescendantNodes().OfType<IdentifierNameSyntax>().FirstOrDefault(id => types.Contains(id.Identifier.Text) && SyntaxFacts.IsInNamespaceOrTypeContext(id) && !id.Ancestors().Any(a => a is UsingDirectiveSyntax));
                if (site is not null) ctx.Add(new Finding("domain-depends-on-infrastructure", "DM6", $"domain type {Cs.TypeName(site)} depends on {site.Identifier.Text} from {Path.GetFileName(dep)} ({depRole}): the domain reaches outward instead of owning a port", rel, Cs.Line(site), null, 1));
            }
        }
        // DM7: repositories over non-roots, or leaking query handles
        foreach (var iface in m.Types.Values.OfType<InterfaceDeclarationSyntax>().Where(i => i.Identifier.Text.EndsWith("Repository")))
        {
            var (p, tree) = m.Owner[iface.Identifier.Text]; var rel = ctx.Workspace.RelPath(tree.FilePath);
            var agg = iface.BaseList?.Types.Select(b => b.Type).OfType<GenericNameSyntax>().FirstOrDefault(g => g.Identifier.Text.Contains("Repository"))?.TypeArgumentList.Arguments[0] is { } ta ? Cs.Simple(ta) : Regex.Replace(iface.Identifier.Text, @"^I|Repository$", "");
            if (m.Entities.Contains(agg) && !roots.Contains(agg) && (ChildOfAnother(m, agg) || m.Roots.Count > 0))
                ctx.Add(new Finding("repository-for-non-aggregate", "DM7", $"{iface.Identifier.Text} is a repository over {agg}, a child entity reached through its aggregate: the root's invariants can be bypassed", rel, Cs.Line(iface), null, 1));
            foreach (var member in iface.Members.OfType<MethodDeclarationSyntax>().Where(md => Cs.Simple(md.ReturnType) is "IQueryable" || md.ReturnType is GenericNameSyntax g && g.Identifier.Text == "Task" && g.TypeArgumentList.Arguments[0] is GenericNameSyntax inner && inner.Identifier.Text == "IQueryable"))
                ctx.Add(new Finding("repository-for-non-aggregate", "DM7", $"{iface.Identifier.Text}.{member.Identifier.Text} returns IQueryable: the port leaks the ORM and its query translation into callers", rel, Cs.Line(member), null, 1));
        }
        // DM9: one rule decided in several places outside the owning type
        ScatteredRules(ctx, m);
        // DM10: one operation writing two aggregates
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath); var model = p.Model(tree);
            foreach (var md in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(x => x.Body is not null))
            {
                var writes = new List<(string repo, string aggregate)>();
                foreach (var inv in md.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
                    var n = ma.Name.Identifier.Text;
                    if (n is not ("Add" or "AddAsync" or "Update" or "UpdateAsync" or "Store" or "StoreAsync" or "Save" or "SaveAsync" or "Upsert" or "UpsertAsync" or "Insert" or "InsertAsync" or "Remove" or "RemoveAsync" or "Delete" or "DeleteAsync")) continue;
                    var receiver = ma.Expression.ToString();
                    var rt = Cs.TypeOf(model, ma.Expression);
                    var rtName = rt is not null && rt.TypeKind != TypeKind.Error ? rt.Name : receiver;
                    if (!Regex.IsMatch(rtName, @"Repository|Repositories|Store$|Set<|DbSet", RegexOptions.IgnoreCase) && !Regex.IsMatch(receiver, @"(?i)repo|repository|store|dbset|\.Set<")) continue;
                    var aggregate = (rt is INamedTypeSymbol nts && nts.TypeKind != TypeKind.Error ? nts.AllInterfaces.Concat(new[] { nts }).FirstOrDefault(i => i.IsGenericType && i.Name.Contains("Repository"))?.TypeArguments.FirstOrDefault()?.Name : null) ?? Regex.Replace(receiver, @"(?i)^_|s$|repository$|repo$", "");
                    writes.Add((receiver, aggregate.ToLowerInvariant()));
                }
                var distinct = writes.Select(w => w.aggregate).Distinct().ToList();
                var commits = md.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => Cs.MemberName(i.Expression) is "SaveChangesAsync" or "SaveChanges" or "CommitAsync" or "Commit" or "CompleteAsync");
                if (distinct.Count >= 2 && (commits || writes.Count >= 2) && !Regex.IsMatch(Cs.TypeName(md), @"(?i)Outbox|Dispatcher|Migration|Seed|Importer|Projection"))
                    ctx.Add(new Finding("multi-aggregate-transaction", "DM10", $"{Cs.TypeName(md)}.{md.Identifier.Text} writes {distinct.Count} aggregates in one unit of work ({string.Join(", ", writes.Select(w => w.repo).Distinct())}): their consistency is now fused", rel, Cs.Line(md.Identifier), null, 1));
            }
        }
        var kloc = ctx.ProductionKloc;
        ctx.Measure("DM1", Shape.FromFindings(ctx.FindingsFor("DM1"), kloc), note: $"{ctx.FindingsFor("DM1").Count()} object reference(s) between aggregates; roots: {string.Join(", ", roots.Take(6))}");
        if (idProps == 0) ctx.Skip("DM2", "no identifier properties on entities"); else ctx.Measure("DM2", Shape.FromShare((double)typedIdProps / idProps), note: $"{typedIdProps} of {idProps} entity id properties strongly typed");
        if (m.IntegrationEvents.Count == 0) ctx.Skip("DM3", "no integration events"); else ctx.Measure("DM3", Shape.FromFindings(ctx.FindingsFor("DM3"), Math.Max(1.0, m.IntegrationEvents.Count / 5.0)), note: $"{ctx.FindingsFor("DM3").Count()} leak(s) across {m.IntegrationEvents.Count} integration events");
        ctx.Measure("DM4", entities == 0 ? 10 : Shape.FromShare(1 - (double)anemic / entities), note: $"{anemic} of {entities} entities anemic");
        ctx.Measure("DM5", Shape.FromFindings(ctx.FindingsFor("DM5"), Math.Max(1.0, entities / 5.0), 0.7), note: $"{ctx.FindingsFor("DM5").Count()} type(s) with mutable state");
        ctx.Measure("DM6", Shape.FromFindings(ctx.FindingsFor("DM6"), kloc), note: $"{ctx.FindingsFor("DM6").Count()} infrastructure dependency(ies) inside the domain");
        ctx.Measure("DM7", Shape.FromFindings(ctx.FindingsFor("DM7"), Math.Max(1.0, Math.Max(1, m.Types.Values.Count(t => t.Identifier.Text.EndsWith("Repository"))) / 4.0)), note: $"{ctx.FindingsFor("DM7").Count()} repository granularity finding(s)");
        ctx.Measure("DM10", Shape.FromFindings(ctx.FindingsFor("DM10"), kloc), note: $"{ctx.FindingsFor("DM10").Count()} multi-aggregate operation(s)");
        ctx.Measure("DM11", Shape.FromFindings(ctx.FindingsFor("DM11"), Math.Max(1.0, entities / 5.0)), note: $"{ctx.FindingsFor("DM11").Count()} unguarded constructor(s)");
        ctx.Measure("DM12", Shape.FromFindings(ctx.FindingsFor("DM12"), kloc), note: $"{ctx.FindingsFor("DM12").Count()} ambient read(s) in the domain");
    }

    private static string MemberName(MemberDeclarationSyntax m) => m switch { PropertyDeclarationSyntax p => p.Identifier.Text, FieldDeclarationSyntax f => f.Declaration.Variables.First().Identifier.Text, _ => "" };

    /// <summary>DM9: the same set of entity properties judged in two or more classes that do not own them.</summary>
    private static void ScatteredRules(ScanContext ctx, Model m)
    {
        var signatures = new Dictionary<string, List<(string cls, string file, int line, string text)>>(StringComparer.Ordinal);
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath); var model = p.Model(tree);
            foreach (var cond in tree.GetRoot().DescendantNodes().Where(n => n is IfStatementSyntax or ConditionalExpressionSyntax or WhenClauseSyntax or ReturnStatementSyntax || n is BinaryExpressionSyntax lb && (lb.IsKind(SyntaxKind.LogicalAndExpression) || lb.IsKind(SyntaxKind.LogicalOrExpression)) && lb.Parent is LambdaExpressionSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax).Select(n => n switch { IfStatementSyntax i => i.Condition, ConditionalExpressionSyntax c => c.Condition, WhenClauseSyntax w => w.Condition, ReturnStatementSyntax r => r.Expression is BinaryExpressionSyntax b && (b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression)) ? r.Expression : null, BinaryExpressionSyntax lb2 => lb2, _ => null }).Where(c => c is not null))
            {
                var cls = Cs.TypeName(cond!);
                var props = new SortedSet<string>(StringComparer.Ordinal); string? entity = null;
                foreach (var ma in cond!.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
                {
                    var rt = Cs.TypeOf(model, ma.Expression);
                    var typeName = rt is not null && rt.TypeKind != TypeKind.Error ? rt.Name : null;
                    if (typeName is null || !m.Entities.Contains(typeName)) continue;
                    entity ??= typeName; if (entity != typeName) continue;
                    props.Add(ma.Name.Identifier.Text);
                }
                if (Environment.GetEnvironmentVariable("CAI_REF_DEBUG") == "dm9") Console.Error.WriteLine($"DM9 {rel}:{Cs.Line(cond)} entity={entity} props={string.Join(",", props)} cls={cls}");
                if (entity is null || props.Count < 2 || cls == entity) continue;
                var key = entity + ":" + string.Join(",", props);
                if (!signatures.TryGetValue(key, out var l)) signatures[key] = l = new();
                l.Add((cls, rel, Cs.Line(cond), Regex.Replace(cond.ToString(), @"\s+", " ")));
            }
        }
        var found = 0;
        foreach (var (key, sites) in signatures.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var classes = sites.Select(s => s.cls).Distinct().ToList();
            if (classes.Count < 2) continue;
            found++;
            var entity = key.Split(':')[0];
            ctx.Add(new Finding("scattered-domain-rule", "DM9", $"the rule over {entity}.{{{key.Split(':')[1]}}} is decided in {classes.Count} places outside {entity} ({string.Join(", ", sites.Select(s => $"{s.cls} at {Path.GetFileName(s.file)}:{s.line}"))}); it belongs on the type that owns the data", null, null, null, 1));
        }
        ctx.Measure("DM9", Shape.FromFindings(ctx.FindingsFor("DM9"), Math.Max(1.0, m.Entities.Count / 5.0)), note: $"{found} scattered rule(s)");
    }

    // ---------------- Event-Driven ----------------
    private static readonly HashSet<string> IrregularPast = new(StringComparer.OrdinalIgnoreCase) { "Held", "Sent", "Paid", "Built", "Made", "Set", "Put", "Begun", "Won", "Lost", "Sold", "Bought", "Taught", "Caught", "Found", "Left", "Kept", "Met", "Read", "Done", "Gone", "Written", "Taken", "Given", "Shown", "Thrown", "Broken", "Chosen", "Frozen", "Hidden", "Forgotten", "Risen", "Spent", "Split", "Cut", "Hit", "Shut", "Let", "Fed", "Led", "Bred", "Spread", "Reset", "Upset", "Overdue", "Withdrawn", "Drawn", "Run", "Rerun", "Begun", "Sung", "Rung", "Swung", "Struck", "Stuck", "Bound", "Wound", "Ground", "Sought", "Brought", "Thought", "Fought", "Dealt", "Felt", "Meant", "Lent", "Bent", "Built", "Lit", "Slid", "Bid", "Quit", "Cast", "Broadcast", "Forecast", "Burst", "Cost", "Hurt", "Shed", "Wed", "Understood", "Overridden", "Rewritten", "Undone", "Redone", "Lain", "Laid", "Said", "Sat", "Stood", "Woken", "Awoken", "Sworn", "Torn", "Worn", "Born", "Shaken", "Forgiven", "Driven", "Eaten", "Fallen", "Grown", "Known", "Flown", "Blown", "Seen", "Been", "Become", "Come", "Overcome", "Become", "Got", "Gotten", "Shot", "Forbidden", "Hung", "Sunk", "Shrunk", "Sprung", "Stung", "Swept", "Wept", "Slept", "Crept", "Leapt", "Learnt", "Burnt", "Dreamt", "Smelt", "Spelt", "Spilt", "Spoilt" };

    private static void Events(ScanContext ctx, Model m)
    {
        // handlers: which class handles which message type
        var handlers = new List<(string handler, string message, string file, int line, MethodDeclarationSyntax? method, ProjectCompilation p, SyntaxTree tree)>();
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var messages = new List<string>();
                foreach (var b in cls.BaseList?.Types ?? default)
                    if (b.Type is GenericNameSyntax g && Regex.IsMatch(g.Identifier.Text, @"^I\w*(Handler|Consumer|Subscriber|Listener)$")) messages.Add(Cs.Simple(g.TypeArgumentList.Arguments[0]));
                var handleMethods = cls.Members.OfType<MethodDeclarationSyntax>().Where(md => Regex.IsMatch(md.Identifier.Text, @"^(Handle|HandleAsync|Consume|ConsumeAsync|On|Process|ProcessAsync)$") && md.ParameterList.Parameters.Count >= 1).ToList();
                if (messages.Count == 0 && cls.Identifier.Text.EndsWith("Handler")) messages.AddRange(handleMethods.Select(h => Cs.Simple(h.ParameterList.Parameters[0].Type)));
                foreach (var msg in messages.Distinct())
                    handlers.Add((cls.Identifier.Text, msg, rel, Cs.Line(cls.Identifier), handleMethods.FirstOrDefault(h => Cs.Simple(h.ParameterList.Parameters[0].Type) == msg) ?? handleMethods.FirstOrDefault(), p, tree));
            }
        }
        // ED1: event handlers that call HTTP/gRPC synchronously (messaging sends are fine)
        var ed1 = 0; var eventHandlers = 0;
        foreach (var h in handlers.Where(h => m.DomainEvents.Contains(h.message) || m.IntegrationEvents.Contains(h.message) || Regex.IsMatch(h.message, @"(Event|ed)$") && !m.Commands.Contains(h.message)))
        {
            if (h.method?.Body is null) continue;
            eventHandlers++;
            var model = h.p.Model(h.tree);
            foreach (var aw in h.method.Body.DescendantNodes().OfType<AwaitExpressionSyntax>())
            {
                var inv = aw.Expression is InvocationExpressionSyntax i ? i : (aw.Expression as InvocationExpressionSyntax);
                var inner = Unwrap(aw.Expression);
                if (inner is not InvocationExpressionSyntax call || call.Expression is not MemberAccessExpressionSyntax ma) continue;
                var name = ma.Name.Identifier.Text;
                var rt = Cs.TypeOf(model, ma.Expression); var rtName = rt is not null && rt.TypeKind != TypeKind.Error ? rt.Name : ma.Expression.ToString();
                var isHttp = rtName is "HttpClient" || Regex.IsMatch(name, @"^(GetFromJsonAsync|PostAsJsonAsync|PutAsJsonAsync|GetStringAsync|GetAsync|PostAsync|PutAsync|DeleteAsync|SendAsync)$") && (rtName.Contains("Http") || rtName.EndsWith("Client") && !Regex.IsMatch(rtName, @"(?i)bus|queue|topic|publisher|producer|sender|session"));
                var isGrpc = rtName.EndsWith("Client") && Regex.IsMatch(name, @"Async$") && Regex.IsMatch(rtName, @"Grpc|\.Client$");
                if (isHttp || isGrpc) { ed1++; ctx.Add(new Finding("synchronous-remote-call-in-event-handler", "ED1", $"{h.handler} awaits {rtName}.{name} while handling {h.message}: the consumer is coupled in time to another service's availability", h.file, Cs.Line(call), null, 1)); break; }
            }
        }
        if (eventHandlers == 0) ctx.Skip("ED1", "no event handlers"); else ctx.Measure("ED1", Shape.FromShare(1 - (double)ed1 / eventHandlers), note: $"{ed1} of {eventHandlers} event handlers make remote calls");
        // ED2: a command with ≠ 1 handler; a domain event raised but handled nowhere
        var ed2 = new List<Finding>();
        foreach (var cmd in m.Commands.OrderBy(x => x, StringComparer.Ordinal))
        {
            var hs = handlers.Where(h => h.message == cmd).Select(h => h.handler).Distinct().ToList();
            var (p, tree) = m.Owner[cmd]; var rel = ctx.Workspace.RelPath(tree.FilePath);
            if (hs.Count > 1) ed2.Add(new Finding("command-with-multiple-handlers", "ED2", $"{cmd} has {hs.Count} handlers ({string.Join(", ", hs)}): a command expresses one intent with one owner; fan-out belongs to an event", rel, Cs.Line(m.Types[cmd]), null, 1));
        }
        foreach (var ev in m.DomainEvents.OrderBy(x => x, StringComparer.Ordinal))
        {
            var (p, tree) = m.Owner[ev]; var rel = ctx.Workspace.RelPath(tree.FilePath); var decl = m.Types[ev];
            var refs = ctx.Workspace.ProductionTrees.SelectMany(t => t.tree.GetRoot().DescendantTokens().Where(tok => tok.IsKind(SyntaxKind.IdentifierToken) && tok.Text == ev && tok != decl.Identifier).Select(tok => (t, tok))).ToList();
            var raised = refs.Any(r => r.tok.Parent is IdentifierNameSyntax id && (id.Parent is ObjectCreationExpressionSyntax || id.Parent is GenericNameSyntax));
            var handled = handlers.Any(h => h.message == ev) || refs.Any(r => r.tok.Parent?.Parent is ParameterSyntax ps && ps.Parent?.Parent is MethodDeclarationSyntax md && md.Identifier.Text is "Apply" or "When" or "On" or "Handle" or "HandleAsync" or "Map" or "ToIntegrationEvent" or "Project" || r.tok.Parent?.Ancestors().Any(a => a is SwitchExpressionArmSyntax or CasePatternSwitchLabelSyntax or IsPatternExpressionSyntax) == true || r.tok.Parent?.Parent is TypeArgumentListSyntax tal && tal.Parent is GenericNameSyntax gn && Regex.IsMatch(gn.Identifier.Text, @"Handler|Consumer|Subscribe|Map|When|On"));
            var mappedByConvention = m.Types.Keys.Any(k => k != ev && k.StartsWith(ev, StringComparison.Ordinal) && Regex.IsMatch(k, "IntegrationEvent$|Message$|Notification$"));
            if (raised && !handled && !mappedByConvention) ed2.Add(new Finding("domain-event-never-handled", "ED2", $"{ev} is raised but nothing handles, folds or maps it: the fact is recorded and ignored", rel, Cs.Line(decl), null, 1));
        }
        foreach (var f in ed2) ctx.Add(f);
        if (m.Commands.Count + m.DomainEvents.Count == 0) ctx.Skip("ED2", "no commands or domain events"); else ctx.Measure("ED2", Shape.FromFindings(ed2, Math.Max(1.0, (m.Commands.Count + m.DomainEvents.Count) / 10.0)), note: $"{ed2.Count} shape finding(s) across {m.Commands.Count} commands and {m.DomainEvents.Count} domain events");
        // ED3: events named in the past tense
        var events = m.DomainEvents.Concat(m.IntegrationEvents).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var ed3 = new List<Finding>();
        foreach (var ev in events)
        {
            var stem = Regex.Replace(ev, @"(Integration)?(Domain)?Event$", "");
            var words = Regex.Split(stem, @"(?<=[a-z0-9])(?=[A-Z])").Where(w => w.Length > 0).ToList();
            if (words.Count == 0) continue;
            var last = words[^1];
            var past = (words.Count > 1 ? words.Skip(1) : words).Any(w => w.EndsWith("ed", StringComparison.OrdinalIgnoreCase) && w.Length > 3 || IrregularPast.Contains(w));   // ConsignmentSentOutForDelivery: the verb need not be the last word
            if (!past) { var (p, tree) = m.Owner[ev]; ed3.Add(new Finding("event-not-named-in-past-tense", "ED3", $"{ev} reads as an instruction, not a fact: an event names what happened (no word of it is a past participle; {last} is not)", ctx.Workspace.RelPath(tree.FilePath), Cs.Line(m.Types[ev]), null, 1)); }
        }
        foreach (var f in ed3) ctx.Add(f);
        if (events.Count == 0) ctx.Skip("ED3", "no event types"); else ctx.Measure("ED3", Shape.FromShare(1 - (double)ed3.Count / events.Count), note: $"{ed3.Count} of {events.Count} events not in the past tense");
        // ED4: commit then publish, no outbox
        var ed4 = new List<Finding>(); var publishers = 0;
        var hasOutbox = ctx.Workspace.ProductionTrees.Any(t => Regex.IsMatch(t.tree.GetRoot().ToString(), @"\bI?Outbox\w*\b|UseOutbox|AddEntityFrameworkOutbox|TransactionalOutbox"));
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var md in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(x => x.Body is not null))
            {
                if (Regex.IsMatch(Cs.TypeName(md), @"(?i)Outbox|Dispatcher|Relay")) continue;
                var invs = md.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
                var commit = invs.FirstOrDefault(i => Cs.MemberName(i.Expression) is "SaveChangesAsync" or "SaveChanges" or "CommitAsync" or "Commit" or "UpdateAsync" or "AddAsync" or "StoreAsync" or "Store");
                var publish = invs.FirstOrDefault(i => Cs.MemberName(i.Expression) is "PublishAsync" or "Publish" or "SendAsync" or "Send" && Regex.IsMatch(Cs.ReceiverText(i.Expression), "(?i)bus|publisher|endpoint|producer|sender|broker|queue|topic|mediator|events"));
                if (publish is null) continue;
                publishers++;
                var usesOutbox = md.Body.ToString().Contains("utbox");
                if (commit is not null && !usesOutbox) ed4.Add(new Finding("dual-write-without-outbox", "ED4", $"{Cs.TypeName(md)}.{md.Identifier.Text} commits state ({Cs.MemberName(commit.Expression)}) and then publishes to the bus directly: a crash between the two loses the message", rel, Cs.Line(md.Identifier), null, 2));
            }
        }
        foreach (var f in ed4) ctx.Add(f);
        if (publishers == 0 && !hasOutbox) ctx.Skip("ED4", "nothing publishes messages"); else ctx.Measure("ED4", Shape.FromFindings(ed4, Math.Max(1.0, publishers / 4.0)), note: $"{ed4.Count} dual write(s) across {publishers} publishing methods; outbox present: {hasOutbox}");
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax e)
    {
        while (true)
        {
            if (e is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "ConfigureAwait") { e = ma.Expression; continue; }
            if (e is ParenthesizedExpressionSyntax p) { e = p.Expression; continue; }
            return e;
        }
    }

    // ---------------- Event Sourcing ----------------
    private static void EventSourcing(ScanContext ctx, Model m)
    {
        var es1 = new List<Finding>(); var folds = 0; var persistedEvents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var md in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(x => x.Identifier.Text is "Apply" or "When" or "On" && x.ParameterList.Parameters.Count == 1))
            {
                var evType = Cs.Simple(md.ParameterList.Parameters[0].Type);
                if (!m.DomainEvents.Contains(evType) && !Regex.IsMatch(evType, @"(ed|Event)$")) continue;
                folds++; persistedEvents.Add(evType);
                var body = Cs.BodyOf(md); if (body is null) continue;
                var ambient = body.DescendantNodes().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(ma => Regex.IsMatch(ma.ToString(), @"^(DateTime|DateTimeOffset)\.(Now|UtcNow|Today)$|^Guid\.NewGuid$|^Random\.Shared|^TimeProvider\.System|^Environment\.(TickCount|MachineName)$|^File\.|^Directory\."));
                var random = body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault(o => Cs.Simple(o.Type) is "Random" or "HttpClient");
                var site = (SyntaxNode?)ambient ?? random;
                if (site is not null) es1.Add(new Finding("nondeterministic-event-fold", "ES1", $"{Cs.TypeName(md)}.{md.Identifier.Text}({evType}) reads {site} while folding: replaying the same stream at another time gives another state", rel, Cs.Line(site), null, 2));
            }
        }
        foreach (var f in es1) ctx.Add(f);
        if (folds == 0) ctx.Skip("ES1", "no Apply/When folds"); else ctx.Measure("ES1", Shape.FromShare(1 - (double)es1.Count / folds), note: $"{es1.Count} of {folds} folds non-deterministic");
        // ES2: persisted events must be immutable; a renamed stored shape needs an upcaster
        var es2 = new List<Finding>();
        var eventTypes = persistedEvents.Where(m.Types.ContainsKey).Concat(m.DomainEvents.Where(e => m.Owner[e].p.Project.Role == ProjectRole.Domain && ctx.Workspace.RelPath(m.Owner[e].tree.FilePath).Contains("/Events/"))).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var allProd = ctx.Workspace.ProductionTrees.Select(t => t.tree.GetRoot().ToString()).ToList();
        // an upcaster is evidence FOR THIS EVENT: a line naming the event beside upcast/version words, or a registration at version ≥ 2
        bool HasUpcaster(string ev) => allProd.Any(s => s.Split('\n').Any(line => line.Contains(ev) && Regex.IsMatch(line, @"(?i)upcast|V\d+To|FromVersion|upgrade") || Regex.IsMatch(line, $@"typeof\({Regex.Escape(ev)}\)[^;]*,\s*([2-9]|\d{{2,}})\s*\)")));
        foreach (var ev in eventTypes)
        {
            var t = m.Types[ev]; var (p, tree) = m.Owner[ev]; var rel = ctx.Workspace.RelPath(tree.FilePath);
            var publicSet = t.Members.OfType<PropertyDeclarationSyntax>().Count(pd => pd.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) && a.Modifiers.Count == 0) == true);
            if (publicSet > 0) es2.Add(new Finding("mutable-persisted-event", "ES2", $"{ev} has {publicSet} public setter(s): stored history can be rewritten through a reference to the event", rel, Cs.Line(t), null, 1));
            // schema change: a positional parameter or property renamed across the file's history
            if (ctx.History.Available && !HasUpcaster(ev))
            {
                Git.Run(ctx.Repo.Root, $"log --follow -p -U0 --format=%x01%h -- \"{rel}\"", out var log);
                static HashSet<string> Members(string diffLines, char sign)
                {
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (Match line in Regex.Matches(diffLines, $@"(?m)^\{sign}(?!\{sign}\{sign})(.*)$"))
                    {
                        var text = line.Groups[1].Value;
                        foreach (Match pm in Regex.Matches(text, @"(?:^|[(,])\s*(?:\[[^\]]*\]\s*)?[\w<>?\[\]., ]+?\s+(\w+)\s*(?=[,)=])")) set.Add(pm.Groups[1].Value);   // positional record parameters
                        var prop = Regex.Match(text, @"^\s*(?:public\s+)?(?:required\s+)?(?:init\s+)?[\w<>?\[\]]+\s+(\w+)\s*\{"); if (prop.Success) set.Add(prop.Groups[1].Value);   // properties
                    }
                    return set;
                }
                // per commit: a member removed and not re-added in the same commit was renamed or dropped after the event had been stored
                var renamed = new List<string>(); var commits = 0;
                foreach (var block in log.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
                {
                    commits++;
                    var removed = Members(block, '-'); var added = Members(block, '+');
                    renamed.AddRange(removed.Except(added).Where(name => char.IsUpper(name[0]) && !Regex.IsMatch(t.ToString(), $@"\b{Regex.Escape(name)}\b")));
                }
                renamed = renamed.Distinct().ToList();
                if (renamed.Count > 0 && commits >= 2)
                    es2.Add(new Finding("event-schema-change-without-upcaster", "ES2", $"{ev}'s stored shape changed after it was first committed ({string.Join(", ", renamed)} removed or renamed) and no upcaster or schema version accompanies the change: events already stored will not deserialise as intended", rel, Cs.Line(t), null, 2));
            }
        }
        foreach (var f in es2) ctx.Add(f);
        if (eventTypes.Count == 0) ctx.Skip("ES2", "no persisted event types"); else ctx.Measure("ES2", Shape.FromFindings(es2, Math.Max(1.0, eventTypes.Count / 5.0)), note: $"{es2.Count} finding(s) across {eventTypes.Count} persisted event types");
    }
}
