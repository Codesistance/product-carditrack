using System.Text;
using System.Text.Json;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The labelling instrument for rollout step 1 of docs/technical/member_chat_routing.md — the step
// that asks for the eval set to be "labelled blind by two people" before the ladder is trusted.
//
// It does two things and deliberately nothing else:
//   sheet  — turns the case fixture into one shuffled, answer-free CSV per labeller
//   score  — reads the filled sheets back and reports inter-labeller agreement, the confusion
//            matrix between labellers, and each labeller against the seed key
//
// sheet and score call no model and touch no database or network. route is the one command that
// does — it sends each case through the real ChatRouterService so the router itself is measured.
// Nothing here ever decides whether the ladder is right — it produces the numbers a human reads
// to decide that. See README.md, and in particular the caveat about what the seed fixture is.

const string CasesFileName = "cases.json";

if (args.Length == 0)
{
    Usage();
    return 1;
}

try
{
    return await (args[0] switch
    {
        "sheet" => Task.FromResult(CmdSheet(args)),
        "score" => Task.FromResult(CmdScore(args)),
        "route" => CmdRouteAsync(args),
        "--help" or "-h" or "help" => Task.FromResult(Usage()),
        _ => Task.FromResult(Fail($"Unknown command '{args[0]}'.")),
    });
}
catch (EvalInputException ex)
{
    // Bad input from a person, not a bug — say what is wrong without a stack trace.
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
catch (TypeInitializationException ex) when (ex.InnerException is EvalInputException inner)
{
    // A catalogue entry with no label id throws from Vocabulary's static initialiser, and the
    // runtime wraps that — so without this clause the fail-fast message above is replaced by an
    // unhandled-exception dump, which is the opposite of failing fast legibly.
    Console.Error.WriteLine($"error: {inner.Message}");
    return 1;
}

int Usage()
{
    Console.WriteLine("""
        CardiTrack chat-routing eval — blind labelling instrument.

          sheet --labeller <name> [--labeller <name> ...] [--out <dir>] [--seed <int>]
              Writes one answer-free CSV per labeller, each independently shuffled, plus a
              LABELLING.md with the vocabulary and the rules. Give it two labellers who have
              not discussed the cases.

          score --labels <file> --labels <file> [--labels <file> ...] [--cases <file>]
              Reads the filled sheets and reports agreement, the confusion matrix, the
              disagreements case by case, and each labeller against the seed key.

          route [--cases <file>]
              Sends every case through the REAL router (ChatRouterService against the configured
              Rewrite-slot model) and scores the answers against the seed key — the router
              measured, not a person. Needs an appsettings.json beside the binary with the same
              Rewrite section AiSplitEvaluator uses, and it spends one model call per case.

        Both commands accept --cases <file> to point at a fixture other than the bundled one.
        """);
    return 0;
}

int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    Console.Error.WriteLine("Run with --help for usage.");
    return 1;
}

// ---------------------------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------------------------

int CmdSheet(string[] args)
{
    var labellers = MultiOption(args, "--labeller");
    var outDir = Option(args, "--out") ?? ".";
    var seed = ParseInt(Option(args, "--seed") ?? "1", "--seed");
    var cases = LoadCases(Option(args, "--cases"));

    if (labellers.Count == 0)
    {
        throw new EvalInputException("sheet needs at least one --labeller <name>.");
    }

    // Two is the number step 1 actually asks for. One sheet is a dry run of the format and cannot
    // produce an agreement rate, so say so rather than quietly emitting a useless artefact.
    if (labellers.Count == 1)
    {
        Console.Error.WriteLine(
            "warning: one labeller produces no agreement rate. Step 1 asks for two people who "
            + "have not discussed the cases.");
    }

    if (labellers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labellers.Count)
    {
        throw new EvalInputException("--labeller names must be distinct; they name the output files.");
    }

    Directory.CreateDirectory(outDir);

    foreach (var labeller in labellers)
    {
        // Shuffled per labeller, and the fixture order is itself grouped by workflow, so an
        // unshuffled sheet would telegraph the answers by adjacency alone.
        var shuffled = Shuffle(cases.Cases, Fnv1a(labeller) ^ (uint)seed);
        var path = Path.Combine(outDir, $"labels-{Slug(labeller)}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("id,question,context,label");
        foreach (var c in shuffled)
        {
            sb.AppendLine($"{Csv(c.Id)},{Csv(c.Question)},{Csv(c.Context)},");
        }

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"wrote {path}  ({shuffled.Count} cases)");
    }

    var instructions = Path.Combine(outDir, "LABELLING.md");
    File.WriteAllText(instructions, BuildInstructions(cases));
    Console.WriteLine($"wrote {instructions}");
    return 0;
}

int CmdScore(string[] args)
{
    var files = MultiOption(args, "--labels");
    var cases = LoadCases(Option(args, "--cases"));

    if (files.Count < 2)
    {
        throw new EvalInputException(
            "score needs at least two --labels files. Agreement between one person and "
            + "themselves is not the measurement step 1 asks for.");
    }

    var byId = cases.Cases.ToDictionary(c => c.Id);
    var sheets = files.Select(f => ReadSheet(f, byId)).ToList();

    Console.WriteLine();
    Console.WriteLine("=== What this is ===");
    Console.WriteLine(Wrap(cases.Caveat));
    Console.WriteLine();
    Console.WriteLine(Wrap(
        "The ~20 % disagreement gate in §10 is stated over REAL caregiver messages. Run against "
        + "the seed fixture this measures whether the taxonomy is teachable from its own "
        + "definitions — a useful dry run, and not evidence about caregivers."));
    Console.WriteLine();

    // --- pairwise agreement -------------------------------------------------------------------
    Console.WriteLine("=== Inter-labeller agreement ===");
    foreach (var (a, b) in Pairs(sheets))
    {
        var shared = a.Labels.Keys.Intersect(b.Labels.Keys).OrderBy(x => x).ToList();
        if (shared.Count == 0)
        {
            Console.WriteLine($"{a.Name} vs {b.Name}: no cases in common.");
            continue;
        }

        var disagreements = shared.Where(id => a.Labels[id] != b.Labels[id]).ToList();
        var rate = (double)disagreements.Count / shared.Count;
        var verdict = rate > 0.20 ? "OVER the ~20 % gate" : "within the ~20 % gate";

        Console.WriteLine(
            $"{a.Name} vs {b.Name}: {shared.Count - disagreements.Count}/{shared.Count} agree, "
            + $"disagreement {rate:P1} — {verdict}");

        if (disagreements.Count > 0)
        {
            Console.WriteLine();
            foreach (var id in disagreements)
            {
                var kind = Classify(a.Labels[id], b.Labels[id]);
                Console.WriteLine($"  [{kind,-9}] {id}  {a.Name}={a.Labels[id]}  {b.Name}={b.Labels[id]}");
                Console.WriteLine($"              \"{byId[id].Question}\"");
            }
            Console.WriteLine();
            Console.WriteLine(Wrap(
                "  tolerable = the analysis/inference superset boundary, which §2's tie-break "
                + "exists to absorb. SERIOUS = one side said advise: that is the boundary where a "
                + "reply starts recommending things, and §11 says it is not tolerable."));
        }
        Console.WriteLine();
    }

    // --- confusion matrix ---------------------------------------------------------------------
    foreach (var (a, b) in Pairs(sheets))
    {
        Console.WriteLine($"=== Confusion matrix — rows {a.Name}, columns {b.Name} ===");
        PrintMatrix(a, b);
        Console.WriteLine();
    }

    // --- against the seed key -------------------------------------------------------------------
    Console.WriteLine("=== Each labeller against the seed key ===");
    Console.WriteLine(Wrap(
        "The key is one author's expectation, so a labeller disagreeing with it is not "
        + "automatically wrong — a case both labellers read the other way is a case the key or the "
        + "ladder has got wrong, which is the outcome step 1 is hunting for."));
    Console.WriteLine();

    foreach (var s in sheets)
    {
        var matched = s.Labels.Count(kv => byId[kv.Key].Key.Contains(kv.Value));
        Console.WriteLine($"{s.Name}: {matched}/{s.Labels.Count} match the key");
        foreach (var kv in s.Labels.Where(kv => !byId[kv.Key].Key.Contains(kv.Value)).OrderBy(kv => kv.Key))
        {
            var c = byId[kv.Key];
            Console.WriteLine($"  {kv.Key}  said={kv.Value}  key={string.Join(" or ", c.Key)}");
            Console.WriteLine($"        \"{c.Question}\"  guards: {c.Guards}");
        }
        Console.WriteLine();
    }

    // Cases where every labeller departed from the key the same way — the strongest signal the
    // fixture can produce, because it points at the key rather than at a labeller.
    var unanimous = byId.Keys
        .Where(id => sheets.All(s => s.Labels.ContainsKey(id)))
        .Where(id => sheets.Select(s => s.Labels[id]).Distinct().Count() == 1)
        .Where(id => !byId[id].Key.Contains(sheets[0].Labels[id]))
        .OrderBy(x => x)
        .ToList();

    if (unanimous.Count > 0)
    {
        Console.WriteLine("=== Every labeller agreed, and all disagreed with the key ===");
        Console.WriteLine(Wrap("Look at the key here before looking at the labellers."));
        Console.WriteLine();
        foreach (var id in unanimous)
        {
            var c = byId[id];
            Console.WriteLine($"  {id}  all said={sheets[0].Labels[id]}  key={string.Join(" or ", c.Key)}");
            Console.WriteLine($"        \"{c.Question}\"");
        }
        Console.WriteLine();
    }

    return 0;
}

async Task<int> CmdRouteAsync(string[] args)
{
    var cases = LoadCases(Option(args, "--cases"));

    // The same wiring a real host uses for the Rewrite slot — the point is to measure the
    // production router, prompt and parser, not a re-implementation of any of them.
    IHost host;
    try
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        builder.Services.AddMedicalAiServices(builder.Configuration);
        host = builder.Build();
    }
    catch (InvalidOperationException ex)
    {
        // Missing model configuration, said in one line — the config's own message names the
        // exact key and the environment-variable spelling.
        throw new EvalInputException(
            $"route needs the Rewrite-slot configuration an appsettings.json provides "
            + $"(same section AiSplitEvaluator uses). {ex.Message}");
    }

    using var _ = host;
    var router = new CardiTrack.Infrastructure.Services.ChatRouterService(
        host.Services.GetRequiredService<IRewriteAiService>());

    Console.WriteLine();
    Console.WriteLine("=== What this is ===");
    Console.WriteLine(Wrap(cases.Caveat));
    Console.WriteLine(Wrap(
        "This run measures the ROUTER against the seed key — routing accuracy on these phrasings, "
        + "not caregiver validity. One model call per case follows."));
    Console.WriteLine();

    var results = new List<(EvalCase Case, string Got, bool Match, bool WouldClarify)>();
    foreach (var c in cases.Cases)
    {
        // Context rows carry their prior exchange as a bare history block; cases without one
        // route on the question alone, exactly as a first turn does.
        var history = c.Context is null ? null : $"--- Earlier turns ---\nCaregiver: {c.Context}";
        string got;
        bool clarify = false;
        try
        {
            var decision = (await router.RouteAsync(c.Question, history)).Result;
            clarify = decision.NeedsClarify;
            got = decision.Primary is { } p
                ? ChatWorkflowCatalogue.Find(p)!.Label
                : "(unparsed \u2192 analysis)";
        }
        catch (Exception ex)
        {
            got = $"(error: {ex.GetType().Name})";
        }

        // The two non-catalogue outcomes cannot be produced by the router, so a case expecting
        // one scores by what should happen instead: inherit-prior rides the history, and a
        // rejected-pre-router case never reaches routing in production at all.
        var match = c.Key.Contains(got);
        results.Add((c, got, match, clarify));
        Console.WriteLine($"  {(match ? "ok  " : "MISS")} {c.Id}  key={string.Join(" or ", c.Key),-28} got={got}{(clarify ? "  [would clarify]" : "")}");
    }

    var scored = results.Where(r => r.Case.Special is null).ToList();
    var hits = scored.Count(r => r.Match);
    Console.WriteLine();
    Console.WriteLine($"Router vs seed key: {hits}/{scored.Count} " +
        $"({(double)hits / scored.Count:P0}); would-clarify on {results.Count(r => r.WouldClarify)} case(s). " +
        $"{results.Count - scored.Count} special-outcome case(s) reported above but not scored.");
    Console.WriteLine();
    Console.WriteLine(Wrap(
        "Read misses through \u00a711's lens: analysis\u2194inference confusion is tolerable by design; "
        + "anything\u2194advise is not."));
    return 0;
}

// ---------------------------------------------------------------------------------------------
// Scoring helpers
// ---------------------------------------------------------------------------------------------

static IEnumerable<(Sheet A, Sheet B)> Pairs(List<Sheet> sheets)
{
    for (var i = 0; i < sheets.Count; i++)
    {
        for (var j = i + 1; j < sheets.Count; j++)
        {
            yield return (sheets[i], sheets[j]);
        }
    }
}

// §11: "analysis↔inference confusion is tolerable by design. Anything↔advise is not."
static string Classify(string a, string b)
{
    if ((a == "advise") ^ (b == "advise"))
    {
        return "SERIOUS";
    }

    string[] superset = ["analysis", "inference"];
    return superset.Contains(a) && superset.Contains(b) ? "tolerable" : "other";
}

void PrintMatrix(Sheet a, Sheet b)
{
    var shared = a.Labels.Keys.Intersect(b.Labels.Keys).ToList();
    var used = shared
        .SelectMany(id => new[] { a.Labels[id], b.Labels[id] })
        .Distinct()
        .OrderBy(Vocabulary.RankOf)
        .ToList();

    if (used.Count == 0)
    {
        Console.WriteLine("  (no cases in common)");
        return;
    }

    var width = Math.Max(14, used.Max(u => u.Length) + 1);
    Console.Write("".PadRight(width));
    foreach (var u in used)
    {
        Console.Write(Abbrev(u).PadLeft(6));
    }
    Console.WriteLine();

    foreach (var row in used)
    {
        Console.Write(row.PadRight(width));
        foreach (var col in used)
        {
            var n = shared.Count(id => a.Labels[id] == row && b.Labels[id] == col);
            Console.Write((n == 0 ? "." : n.ToString()).PadLeft(6));
        }
        Console.WriteLine();
    }

    Console.WriteLine();
    Console.WriteLine("  columns: " + string.Join("  ", used.Select(u => $"{Abbrev(u)}={u}")));
}

static string Abbrev(string label) => label switch
{
    "status" => "stat",
    "analysis" => "anly",
    "inference" => "infr",
    "investigation" => "invs",
    "advise" => "advs",
    "steer.casual" => "s.cas",
    "steer.offtopic" => "s.off",
    "clarify" => "clar",
    "inherit-prior" => "inhr",
    "rejected-pre-router" => "rej",
    _ => label.Length <= 5 ? label : label[..5],
};

// ---------------------------------------------------------------------------------------------
// I/O
// ---------------------------------------------------------------------------------------------

Sheet ReadSheet(string path, Dictionary<string, EvalCase> byId)
{
    if (!File.Exists(path))
    {
        throw new EvalInputException($"No such labels file: {path}");
    }

    var name = Path.GetFileNameWithoutExtension(path);
    if (name.StartsWith("labels-", StringComparison.Ordinal))
    {
        name = name["labels-".Length..];
    }

    var labels = new Dictionary<string, string>();
    var lines = File.ReadAllLines(path);
    var blank = 0;

    for (var i = 1; i < lines.Length; i++)  // row 0 is the header
    {
        if (string.IsNullOrWhiteSpace(lines[i]))
        {
            continue;
        }

        var f = ParseCsvLine(lines[i]);
        if (f.Count < 4)
        {
            throw new EvalInputException($"{path} line {i + 1}: expected 4 columns, got {f.Count}.");
        }

        var (id, label) = (f[0].Trim(), f[3].Trim());

        if (!byId.ContainsKey(id))
        {
            throw new EvalInputException($"{path} line {i + 1}: unknown case id '{id}'.");
        }

        if (label.Length == 0)
        {
            blank++;
            continue;
        }

        if (!Vocabulary.All.Contains(label))
        {
            throw new EvalInputException(
                $"{path} line {i + 1}: '{label}' is not in the vocabulary. Allowed: "
                + string.Join(", ", Vocabulary.All));
        }

        if (!labels.TryAdd(id, label))
        {
            throw new EvalInputException($"{path} line {i + 1}: case '{id}' labelled twice.");
        }
    }

    // Blanks are dropped from the agreement denominator, which quietly flatters the rate — a
    // half-finished sheet would otherwise score as strong agreement on the part that got done.
    if (blank > 0)
    {
        Console.Error.WriteLine(
            $"warning: {name} left {blank} case(s) unlabelled; they are excluded from every "
            + "figure below, so the rates are over a smaller set than the fixture.");
    }

    if (labels.Count == 0)
    {
        throw new EvalInputException($"{path} has no labelled rows.");
    }

    return new Sheet(name, labels);
}

CaseFile LoadCases(string? explicitPath)
{
    var path = explicitPath
        ?? Path.Combine(AppContext.BaseDirectory, CasesFileName);

    if (!File.Exists(path))
    {
        throw new EvalInputException($"No case fixture at {path}.");
    }

    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var file = JsonSerializer.Deserialize<CaseFile>(File.ReadAllText(path), opts)
        ?? throw new EvalInputException($"{path} did not parse as a case fixture.");

    if (file.Cases.Length == 0)
    {
        throw new EvalInputException($"{path} contains no cases.");
    }

    var dupes = file.Cases.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    if (dupes.Count > 0)
    {
        throw new EvalInputException($"{path} has duplicate case ids: {string.Join(", ", dupes)}.");
    }

    // A key outside the vocabulary would score every labeller wrong on that case and read as a
    // labelling failure rather than the fixture bug it is.
    foreach (var c in file.Cases)
    {
        foreach (var k in c.Key)
        {
            if (!Vocabulary.All.Contains(k))
            {
                throw new EvalInputException(
                    $"{path}: case {c.Id} expects '{k}', which is not in the vocabulary.");
            }
        }
    }

    return file;
}

string BuildInstructions(CaseFile cases)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Labelling the chat-routing cases");
    sb.AppendLine();
    sb.AppendLine("You have a CSV of caregiver questions with an empty `label` column. Fill it in.");
    sb.AppendLine();
    sb.AppendLine("**Do not discuss the cases with the other labeller until both sheets are in.**");
    sb.AppendLine("The whole point is two independent readings; comparing notes first destroys the");
    sb.AppendLine("measurement and there is no way to recover it afterwards.");
    sb.AppendLine();
    sb.AppendLine("Answer the question \"which of these should the app do?\" — not \"which would it");
    sb.AppendLine("do today\". If two feel equally right, pick the one that claims less.");
    sb.AppendLine();
    sb.AppendLine("Some rows have a `context` column. That is what the caregiver had just been told;");
    sb.AppendLine("read the question as a follow-up to it.");
    sb.AppendLine();
    sb.AppendLine("## The labels");
    sb.AppendLine();
    foreach (var (id, meaning) in Vocabulary.Described())
    {
        sb.AppendLine($"- **`{id}`** — {meaning}");
        sb.AppendLine();
    }
    sb.AppendLine("Write the label exactly as spelled above. Leave a row blank only if you truly");
    sb.AppendLine("cannot choose — blanks are reported separately and are themselves a finding.");
    sb.AppendLine();
    sb.AppendLine("## What happens next");
    sb.AppendLine();
    sb.AppendLine("Both sheets go through `score`, which reports where you two differ. Disagreement");
    sb.AppendLine("above roughly 20 % means the taxonomy is unclear — that is a finding about the");
    sb.AppendLine("design, not about either of you.");
    sb.AppendLine();
    sb.AppendLine("## About this fixture");
    sb.AppendLine();
    sb.AppendLine(cases.Caveat);
    sb.AppendLine();
    sb.AppendLine($"Source: {cases.Source}");
    return sb.ToString();
}

// ---------------------------------------------------------------------------------------------
// Small utilities
// ---------------------------------------------------------------------------------------------

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    if (i < 0)
    {
        return null;
    }

    if (i + 1 >= args.Length)
    {
        throw new EvalInputException($"{name} needs a value.");
    }

    return args[i + 1];
}

static List<string> MultiOption(string[] args, string name)
{
    var values = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] != name)
        {
            continue;
        }

        if (i + 1 >= args.Length)
        {
            throw new EvalInputException($"{name} needs a value.");
        }

        values.Add(args[i + 1]);
    }

    return values;
}

static int ParseInt(string raw, string name) =>
    int.TryParse(raw, out var v) ? v : throw new EvalInputException($"{name} must be an integer (got '{raw}').");

/// <summary>FNV-1a — a stable hash. <c>string.GetHashCode</c> is randomised per process, which
/// would reshuffle a labeller's sheet on every run and make a re-issued sheet unreproducible.</summary>
static uint Fnv1a(string s)
{
    var hash = 2166136261u;
    foreach (var ch in s)
    {
        hash = (hash ^ ch) * 16777619u;
    }

    return hash;
}

/// <summary>Fisher–Yates over an explicit xorshift, so a given (labeller, seed) always produces
/// the same sheet — across machines and across .NET versions, which <c>Random(int)</c> does not
/// promise.</summary>
static List<EvalCase> Shuffle(EvalCase[] cases, uint seed)
{
    var state = seed == 0 ? 0x9E3779B9u : seed;
    var list = cases.ToList();

    for (var i = list.Count - 1; i > 0; i--)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        var j = (int)(state % (uint)(i + 1));
        (list[i], list[j]) = (list[j], list[i]);
    }

    return list;
}

static string Slug(string s) =>
    new(s.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray());

static string Csv(string? s)
{
    // Newlines are flattened, not quoted: the whole tool is line-based — ReadSheet reads one
    // physical line per case — so a quoted multi-line field would emit a sheet score cannot
    // parse back. The bundled fixture is single-line by construction, but --cases accepts
    // arbitrary fixtures and the real eval set's caregiver phrasings may not be.
    s = (s ?? "").ReplaceLineEndings(" ");
    return s.Contains(',') || s.Contains('"')
        ? "\"" + s.Replace("\"", "\"\"") + "\""
        : s;
}

static List<string> ParseCsvLine(string line)
{
    var fields = new List<string>();
    var sb = new StringBuilder();
    var quoted = false;

    for (var i = 0; i < line.Length; i++)
    {
        var ch = line[i];

        if (quoted)
        {
            if (ch != '"')
            {
                sb.Append(ch);
            }
            else if (i + 1 < line.Length && line[i + 1] == '"')
            {
                sb.Append('"');
                i++;
            }
            else
            {
                quoted = false;
            }
        }
        else if (ch == '"')
        {
            quoted = true;
        }
        else if (ch == ',')
        {
            fields.Add(sb.ToString());
            sb.Clear();
        }
        else
        {
            sb.Append(ch);
        }
    }

    fields.Add(sb.ToString());
    return fields;
}

static string Wrap(string text, int width = 92)
{
    var sb = new StringBuilder();
    var line = new StringBuilder();

    foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        if (line.Length > 0 && line.Length + 1 + word.Length > width)
        {
            sb.AppendLine(line.ToString());
            line.Clear();
        }

        if (line.Length > 0)
        {
            line.Append(' ');
        }

        line.Append(word);
    }

    if (line.Length > 0)
    {
        sb.Append(line);
    }

    return sb.ToString();
}

// ---------------------------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------------------------

/// <summary>Bad input from a person running the tool, as opposed to a defect in it.</summary>
sealed class EvalInputException(string message) : Exception(message);

// ---------------------------------------------------------------------------------------------
// Vocabulary
// ---------------------------------------------------------------------------------------------

// Deliberately built from ChatWorkflowCatalogue.All, NOT .Routable. Routable is what the routing
// prompt renders today, which excludes `investigation` because its handler is unbuilt — but the
// eval set is precisely what decides whether that handler gets built, so a labeller must be able
// to choose it.
//
// The label strings are the catalogue's own, not a copy: ChatWorkflowDefinition.Label is a required
// parameter, so an entry with no external name fails to compile rather than reaching a sheet
// unnamed. The Steer filter is defensive — that retired id is not in the catalogue today and no
// test forbids re-adding it, so this keeps a re-added one off the sheet rather than reacting to it.
static class Vocabulary
{
    /// <summary>Outcomes a labeller may pick that are not catalogue entries at all.</summary>
    public static readonly Dictionary<string, string> Special = new()
    {
        ["inherit-prior"] = "A follow-up carrying its subject from the previous turn — the right "
            + "answer is whatever the previous turn was.",
        ["rejected-pre-router"] = "Must never reach routing at all (injection, prompt extraction).",
    };

    /// <summary>Every label a filled sheet may legally contain.</summary>
    public static IReadOnlyList<string> All { get; } =
        ChatWorkflowCatalogue.All
            .Where(w => w.Id != MemberChatWorkflow.Steer)
            .Select(w => w.Label)
            .Concat(Special.Keys)
            .ToArray();

    // Declared after All so the static initialiser fills it from a populated list. Exists because
    // the display order was being recomputed inside an OrderBy key selector, which rebuilt the
    // whole vocabulary array once per element compared.
    private static readonly Dictionary<string, int> Ranks =
        All.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);

    /// <summary>Catalogue order, for display: the ladder, then the off-ladder outcomes. An
    /// unknown label sorts last rather than throwing — ordering a report is not where a bad
    /// label should surface.</summary>
    public static int RankOf(string label) => Ranks.TryGetValue(label, out var r) ? r : int.MaxValue;

    /// <summary>What each label means, for the labelling sheet — the catalogue's own purpose
    /// line, so the instructions cannot drift from what the router will be told.</summary>
    public static IEnumerable<(string Id, string Meaning)> Described() =>
        ChatWorkflowCatalogue.All
            .Where(w => w.Id != MemberChatWorkflow.Steer)
            .Select(w => (w.Label, w.Purpose))
            .Concat(Special.Select(kv => (kv.Key, kv.Value)));
}

// ---------------------------------------------------------------------------------------------
// Fixture
// ---------------------------------------------------------------------------------------------

sealed record EvalCase(
    string Id,
    string Question,
    string? Context,
    string[] Accepted,
    string? Special,
    string? Note,
    string Guards)
{
    /// <summary>The labels the seed key treats as correct — several, where the ladder's superset
    /// rule makes neighbouring rungs both defensible.</summary>
    public IReadOnlyList<string> Key => Special is null ? Accepted : [Special];
}

sealed record CaseFile(int Version, string Source, string Caveat, EvalCase[] Cases);

sealed record Sheet(string Name, Dictionary<string, string> Labels);
