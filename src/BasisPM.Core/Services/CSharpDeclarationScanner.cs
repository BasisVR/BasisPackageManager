using System.Text;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class CSharpDeclarationScanner
{
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "internal", "private", "protected", "static", "sealed", "abstract", "partial", "readonly", "ref", "unsafe", "new", "file", "extern", "virtual", "override", "required", "scoped",
    };

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal) { "class", "struct", "interface", "enum", "record", "delegate" };

    private static readonly HashSet<string> EditorOnlyTypes = new(StringComparer.Ordinal)
    {
        "AssetDatabase", "EditorUtility", "EditorGUILayout", "EditorGUI", "EditorGUIUtility", "EditorWindow", "EditorApplication", "EditorPrefs", "EditorStyles", "EditorSceneManager",
        "PrefabUtility", "SerializedObject", "SerializedProperty", "AssetPostprocessor", "BuildPipeline", "PlayerSettings", "CustomEditor", "CustomPropertyDrawer", "PropertyDrawer",
        "MenuItem", "InitializeOnLoad", "InitializeOnLoadMethod", "SceneView", "Undo", "Handles", "ScriptableWizard", "EditorBuildSettings", "AssetImporter", "TextureImporter",
        "ModelImporter", "ProjectWindowUtil", "EditorJsonUtility", "MonoScript", "CanEditMultipleObjects", "DrawGizmo", "BuildTarget", "BuildTargetGroup", "EditorUserBuildSettings",
        "AssetPreview", "EditorCurveBinding", "AnimationUtility", "SerializedPropertyType", "ShaderUtil", "Lightmapping", "StageUtility", "PrefabStageUtility",
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> EditorOnlyLookup = EditorOnlyTypes.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly HashSet<string> PlayerEditorNamespaces = new(StringComparer.Ordinal) { "Experimental", "Analytics" };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> PlayerEditorLookup = PlayerEditorNamespaces.GetAlternateLookup<ReadOnlySpan<char>>();

    public static CSharpSourceInfo Scan(string source) => new DeclarationParser(source ?? "").Run();

    public static bool IsEditorGuard(string condition) => EditorGuard(condition, positive: true);

    private static bool EditorGuard(string condition, bool positive)
    {
        var index = 0;
        while ((index = condition.IndexOf("UNITY_EDITOR", index, StringComparison.Ordinal)) >= 0)
        {
            var before = index - 1;
            while (before >= 0 && char.IsWhiteSpace(condition[before])) before--;
            var negated = before >= 0 && condition[before] == '!';
            var startsWord = index == 0 || !(char.IsLetterOrDigit(condition[index - 1]) || condition[index - 1] == '_');
            if (startsWord && negated != positive) return true;
            index += "UNITY_EDITOR".Length;
        }
        return false;
    }

    private enum DeclarationTokenKind { End, Identifier, Punct, Literal }

    private enum DeclarationScopeKind { Namespace, Type, Other }

    private readonly record struct DeclarationToken(DeclarationTokenKind Kind, int Start, int Length, char Punct, int Line);

    private readonly record struct DeclarationWord(DeclarationTokenKind Kind, string Text, char Punct, int Line)
    {
        public bool Is(char punct) => Kind == DeclarationTokenKind.Punct && Punct == punct;
        public bool Is(string word) => Kind == DeclarationTokenKind.Identifier && Text == word;
    }

    private readonly record struct DeclarationScope(DeclarationScopeKind Kind, string Namespace);

    private sealed class DeclarationSnapshot
    {
        public required DeclarationScope[] Scopes { get; init; }
        public required DeclarationWord[] Statement { get; init; }
        public required string FileNamespace { get; init; }
        public required int AttributeDepth { get; init; }
    }

    private sealed class DeclarationFrame
    {
        public bool Guarded;
        public bool NegatedIf;
        public bool InFirstBranch = true;
        public required DeclarationSnapshot AtIf;
        public DeclarationSnapshot? AfterFirst;
    }

    private sealed class DeclarationParser(string source)
    {
        private const int StatementLimit = 512;
        private readonly string s = source;
        private readonly List<CSharpTypeDeclaration> types = new();
        private readonly List<string> namespaces = new();
        private readonly HashSet<string> namespaceSet = new(StringComparer.Ordinal);
        private readonly List<CSharpUsingDirective> usings = new();
        private readonly List<int> editorLines = new();
        private readonly List<DeclarationScope> scopes = new();
        private readonly List<DeclarationWord> statement = new();
        private readonly List<DeclarationFrame> frames = new();
        private string fileNamespace = "";
        private int attributeDepth;
        private int i;
        private int line = 1;
        private bool atLineStart = true;
        private char previousPunct;
        private bool previousWasNamespace;
        private bool previousWasTypeKeyword;
        private int qualifiedState;
        private int qualifiedLine;
        private bool anyEditorUsing;
        private string? pendingName;
        private int pendingLine;
        private char pendingPrevious;
        private readonly List<int> qualifiedLines = new();
        private readonly List<CSharpEditorUse> editorUses = new();
        private readonly List<int> editorUsingLines = new();

        private bool Conditional => frames.Count > 0;
        private bool EditorGuarded => frames.Any(f => f.Guarded);
        private bool AtNamespaceLevel => scopes.Count == 0 || scopes[^1].Kind == DeclarationScopeKind.Namespace;
        private string CurrentNamespace => scopes.Count > 0 ? scopes[^1].Namespace : fileNamespace;

        public CSharpSourceInfo Run()
        {
            while (true)
            {
                var token = Next();
                if (token.Kind == DeclarationTokenKind.End) break;
                TrackEditorUse(token);
                if (token.Kind == DeclarationTokenKind.Identifier)
                {
                    var word = s.AsSpan(token.Start, token.Length);
                    previousWasNamespace = word.SequenceEqual("namespace");
                    previousWasTypeKeyword = word is "class" or "struct" or "interface" or "enum" or "record" or "delegate";
                    previousPunct = '\0';
                }
                else
                {
                    previousWasNamespace = false;
                    previousWasTypeKeyword = false;
                    previousPunct = token.Kind == DeclarationTokenKind.Punct ? token.Punct : '\0';
                }

                if (!AtNamespaceLevel)
                {
                    if (token.Kind != DeclarationTokenKind.Punct) continue;
                    if (token.Punct == '{') scopes.Add(new DeclarationScope(DeclarationScopeKind.Other, CurrentNamespace));
                    else if (token.Punct == '}' && scopes.Count > 0)
                    {
                        scopes.RemoveAt(scopes.Count - 1);
                        if (AtNamespaceLevel) statement.Clear();
                    }
                    continue;
                }
                OnNamespaceLevel(token);
            }
            ResolvePending(new DeclarationToken(DeclarationTokenKind.End, i, 0, '\0', line));
            editorLines.AddRange(qualifiedLines.Distinct().Order());
            return new CSharpSourceInfo(types, namespaces, usings, editorLines)
            {
                UnguardedEditorUsings = editorUsingLines,
                EditorTypeUses = anyEditorUsing ? editorUses : Array.Empty<CSharpEditorUse>(),
            };
        }

        private bool InUsingStatement => AtNamespaceLevel && statement.Count > 0 && (statement[0].Is("using") || (statement[0].Is("global") && statement.Count > 1 && statement[1].Is("using")));

        private void ResolvePending(DeclarationToken next)
        {
            if (pendingName is null) return;
            var accept = pendingPrevious is '[' or ':'
                || next.Kind == DeclarationTokenKind.Identifier
                || (next.Kind == DeclarationTokenKind.Punct && next.Punct is '.' or '(')
                || (next.Kind == DeclarationTokenKind.Punct && next.Punct is '>' or ',' && pendingPrevious is '<' or ',');
            if (accept) editorUses.Add(new CSharpEditorUse(pendingLine, pendingName));
            pendingName = null;
        }

        private void TrackEditorUse(DeclarationToken token)
        {
            ResolvePending(token);
            if (token.Kind != DeclarationTokenKind.Identifier)
            {
                qualifiedState = token.Kind == DeclarationTokenKind.Punct && token.Punct == '.' && qualifiedState == 1 ? 2 : 0;
                return;
            }
            var word = s.AsSpan(token.Start, token.Length);
            if (qualifiedState == 2)
            {
                qualifiedState = 0;
                if (!PlayerEditorLookup.Contains(word)) qualifiedLines.Add(qualifiedLine);
                return;
            }
            qualifiedState = 0;
            var guarded = EditorGuarded;
            if (word.SequenceEqual("UnityEditor"))
            {
                if (previousPunct == '.' || previousWasNamespace) return;
                if (InUsingStatement)
                {
                    anyEditorUsing = true;
                    if (!guarded && (editorUsingLines.Count == 0 || editorUsingLines[^1] != token.Line)) editorUsingLines.Add(token.Line);
                    return;
                }
                if (guarded) return;
                qualifiedState = 1;
                qualifiedLine = token.Line;
                return;
            }
            if (guarded || InUsingStatement || previousPunct == '.' || previousWasTypeKeyword) return;
            if (EditorOnlyLookup.Contains(word) || (previousPunct == ':' && word.SequenceEqual("Editor")))
            {
                pendingName = word.ToString();
                pendingLine = token.Line;
                pendingPrevious = previousPunct;
            }
        }

        private void OnNamespaceLevel(DeclarationToken token)
        {
            if (attributeDepth > 0)
            {
                if (token.Kind == DeclarationTokenKind.Punct && token.Punct == '[') attributeDepth++;
                else if (token.Kind == DeclarationTokenKind.Punct && token.Punct == ']') attributeDepth--;
                return;
            }
            if (token.Kind == DeclarationTokenKind.Punct)
            {
                switch (token.Punct)
                {
                    case '[' when statement.Count == 0:
                        attributeDepth = 1;
                        return;
                    case '{':
                        OpenBlock();
                        return;
                    case ';':
                        EndStatement();
                        return;
                    case '}':
                        if (scopes.Count > 0) scopes.RemoveAt(scopes.Count - 1);
                        statement.Clear();
                        return;
                }
            }
            if (statement.Count < StatementLimit)
                statement.Add(new DeclarationWord(token.Kind, token.Kind == DeclarationTokenKind.Identifier ? s.Substring(token.Start, token.Length) : "", token.Punct, token.Line));
        }

        private void OpenBlock()
        {
            var current = CurrentNamespace;
            var header = ReadHeader(out var name, out var declaration);
            if (header == DeclarationScopeKind.Namespace && name is not null)
            {
                var full = current.Length == 0 ? name : current + "." + name;
                AddNamespace(full);
                scopes.Add(new DeclarationScope(DeclarationScopeKind.Namespace, full));
            }
            else
            {
                if (header == DeclarationScopeKind.Type && declaration is not null) types.Add(declaration with { Namespace = current });
                scopes.Add(new DeclarationScope(header == DeclarationScopeKind.Type ? DeclarationScopeKind.Type : DeclarationScopeKind.Other, current));
            }
            statement.Clear();
        }

        private void EndStatement()
        {
            if (statement.Count == 0) return;
            if (statement[0].Is("using") || (statement.Count > 1 && statement[0].Is("global") && statement[1].Is("using"))) ReadUsing();
            else
            {
                var current = CurrentNamespace;
                var header = ReadHeader(out var name, out var declaration);
                if (header == DeclarationScopeKind.Namespace && name is not null && scopes.Count == 0)
                {
                    fileNamespace = name;
                    AddNamespace(name);
                }
                else if (header == DeclarationScopeKind.Type && declaration is not null && (declaration.Kind is "delegate" || declaration.Kind.StartsWith("record", StringComparison.Ordinal)))
                    types.Add(declaration with { Namespace = current });
            }
            statement.Clear();
        }

        private void ReadUsing()
        {
            var index = 0;
            var isGlobal = false;
            if (statement[0].Is("global"))
            {
                isGlobal = true;
                index++;
            }
            index++;
            var isStatic = index < statement.Count && statement[index].Is("static");
            if (isStatic) index++;
            string? alias = null;
            if (index + 1 < statement.Count && statement[index].Kind == DeclarationTokenKind.Identifier && statement[index + 1].Is('='))
            {
                alias = statement[index].Text;
                index += 2;
            }
            var target = new StringBuilder();
            var lineOfTarget = index < statement.Count ? statement[index].Line : statement[0].Line;
            for (; index < statement.Count; index++)
            {
                var word = statement[index];
                if (word.Kind == DeclarationTokenKind.Identifier) target.Append(word.Text);
                else if (word.Kind == DeclarationTokenKind.Punct && word.Punct is '.' or ':' or '<' or '>' or ',') target.Append(word.Punct);
                else if (word.Kind == DeclarationTokenKind.Punct && word.Punct is '(' or '=') return;
            }
            if (target.Length == 0) return;
            usings.Add(new CSharpUsingDirective(target.ToString(), alias, isStatic, isGlobal, Conditional, lineOfTarget) { Scope = CurrentNamespace });
        }

        private DeclarationScopeKind ReadHeader(out string? name, out CSharpTypeDeclaration? declaration)
        {
            name = null;
            declaration = null;
            var index = 0;
            bool isPublic = false, isFile = false;
            while (index < statement.Count && statement[index].Kind == DeclarationTokenKind.Identifier && Modifiers.Contains(statement[index].Text))
            {
                if (statement[index].Text == "public") isPublic = true;
                else if (statement[index].Text == "file") isFile = true;
                index++;
            }
            if (index >= statement.Count || statement[index].Kind != DeclarationTokenKind.Identifier) return DeclarationScopeKind.Other;
            var keyword = statement[index].Text;
            if (keyword == "namespace")
            {
                name = ReadDotted(index + 1);
                return name is null ? DeclarationScopeKind.Other : DeclarationScopeKind.Namespace;
            }
            if (!TypeKeywords.Contains(keyword)) return DeclarationScopeKind.Other;
            if (keyword == "delegate") return ReadDelegate(index + 1, isPublic, isFile, out declaration);
            var kind = keyword;
            index++;
            if (keyword == "record" && index < statement.Count && (statement[index].Is("class") || statement[index].Is("struct")))
            {
                kind = "record " + statement[index].Text;
                index++;
            }
            if (index >= statement.Count || statement[index].Kind != DeclarationTokenKind.Identifier) return DeclarationScopeKind.Other;
            var typeName = statement[index];
            var arity = index + 1 < statement.Count && statement[index + 1].Is('<') ? CountArity(index + 1) : 0;
            declaration = new CSharpTypeDeclaration("", typeName.Text, arity, kind, isPublic, isFile, Conditional, typeName.Line);
            return DeclarationScopeKind.Type;
        }

        private DeclarationScopeKind ReadDelegate(int index, bool isPublic, bool isFile, out CSharpTypeDeclaration? declaration)
        {
            declaration = null;
            var depth = 0;
            var open = -1;
            for (var k = index; k < statement.Count; k++)
            {
                if (statement[k].Is('<')) depth++;
                else if (statement[k].Is('>')) depth--;
                else if (statement[k].Is('(') && depth == 0)
                {
                    open = k;
                    break;
                }
            }
            if (open <= index) return DeclarationScopeKind.Other;
            var nameIndex = open - 1;
            var arity = 0;
            if (statement[nameIndex].Is('>'))
            {
                var close = nameIndex;
                var nesting = 0;
                for (var k = close; k > index; k--)
                {
                    if (statement[k].Is('>')) nesting++;
                    else if (statement[k].Is('<') && --nesting == 0)
                    {
                        arity = CountArity(k);
                        nameIndex = k - 1;
                        break;
                    }
                }
            }
            if (nameIndex < index || statement[nameIndex].Kind != DeclarationTokenKind.Identifier) return DeclarationScopeKind.Other;
            declaration = new CSharpTypeDeclaration("", statement[nameIndex].Text, arity, "delegate", isPublic, isFile, Conditional, statement[nameIndex].Line);
            return DeclarationScopeKind.Type;
        }

        private int CountArity(int open)
        {
            var depth = 0;
            var commas = 0;
            for (var k = open; k < statement.Count; k++)
            {
                if (statement[k].Is('<') || statement[k].Is('(') || statement[k].Is('[')) depth++;
                else if (statement[k].Is('>') || statement[k].Is(')') || statement[k].Is(']'))
                {
                    if (--depth == 0) return commas + 1;
                }
                else if (statement[k].Is(',') && depth == 1) commas++;
            }
            return commas + 1;
        }

        private string? ReadDotted(int index)
        {
            var name = new StringBuilder();
            for (; index < statement.Count; index++)
            {
                var word = statement[index];
                if (word.Kind == DeclarationTokenKind.Identifier) name.Append(word.Text);
                else if (word.Is('.')) name.Append('.');
                else break;
            }
            var text = name.ToString().Trim('.');
            return text.Length == 0 ? null : text;
        }

        private void AddNamespace(string full)
        {
            var dot = -1;
            while ((dot = full.IndexOf('.', dot + 1)) >= 0)
            {
                var prefix = full[..dot];
                if (namespaceSet.Add(prefix)) namespaces.Add(prefix);
            }
            if (namespaceSet.Add(full)) namespaces.Add(full);
        }

        private DeclarationSnapshot Snapshot() => new()
        {
            Scopes = scopes.ToArray(),
            Statement = statement.ToArray(),
            FileNamespace = fileNamespace,
            AttributeDepth = attributeDepth,
        };

        private void Restore(DeclarationSnapshot snapshot)
        {
            scopes.Clear();
            scopes.AddRange(snapshot.Scopes);
            statement.Clear();
            statement.AddRange(snapshot.Statement);
            fileNamespace = snapshot.FileNamespace;
            attributeDepth = snapshot.AttributeDepth;
        }

        private void Directive(string name, string condition)
        {
            switch (name)
            {
                case "if":
                    frames.Add(new DeclarationFrame { Guarded = EditorGuard(condition, true), NegatedIf = EditorGuard(condition, false), AtIf = Snapshot() });
                    break;
                case "elif":
                case "else":
                    if (frames.Count == 0) break;
                    var frame = frames[^1];
                    if (frame.InFirstBranch)
                    {
                        frame.AfterFirst = Snapshot();
                        frame.InFirstBranch = false;
                    }
                    Restore(frame.AtIf);
                    frame.Guarded = name == "elif" ? EditorGuard(condition, true) : frame.NegatedIf;
                    break;
                case "endif":
                    if (frames.Count == 0) break;
                    var last = frames[^1];
                    frames.RemoveAt(frames.Count - 1);
                    if (!last.InFirstBranch && last.AfterFirst is not null) Restore(last.AfterFirst);
                    break;
            }
        }

        private DeclarationToken Next()
        {
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\n')
                {
                    line++;
                    i++;
                    atLineStart = true;
                    continue;
                }
                if (c is ' ' or '\t' or '\r' or '\f' or '\v' or '\uFEFF' || char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }
                if (c == '#' && atLineStart)
                {
                    ReadDirective();
                    continue;
                }
                atLineStart = false;
                var next = i + 1 < s.Length ? s[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    SkipLine();
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    SkipBlockComment();
                    continue;
                }
                var startLine = line;
                if (IsStringStart(c, next))
                {
                    var start = i;
                    SkipString();
                    return new DeclarationToken(DeclarationTokenKind.Literal, start, i - start, '\0', startLine);
                }
                if (c == '\'')
                {
                    var start = i;
                    SkipChar();
                    return new DeclarationToken(DeclarationTokenKind.Literal, start, i - start, '\0', startLine);
                }
                if (IsIdentifierStart(c) || (c == '@' && IsIdentifierStart(next)))
                {
                    if (c == '@') i++;
                    var start = i;
                    while (i < s.Length && IsIdentifierPart(s[i])) i++;
                    return new DeclarationToken(DeclarationTokenKind.Identifier, start, i - start, '\0', startLine);
                }
                if (char.IsDigit(c))
                {
                    var start = i;
                    while (i < s.Length && (IsIdentifierPart(s[i]) || (s[i] == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))) i++;
                    return new DeclarationToken(DeclarationTokenKind.Literal, start, i - start, '\0', startLine);
                }
                i++;
                return new DeclarationToken(DeclarationTokenKind.Punct, i - 1, 1, c, startLine);
            }
            return new DeclarationToken(DeclarationTokenKind.End, i, 0, '\0', line);
        }

        private void ReadDirective()
        {
            i++;
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            var start = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            var name = s[start..i];
            var restStart = i;
            while (i < s.Length && s[i] != '\n') i++;
            var condition = s[restStart..i];
            var comment = condition.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) condition = condition[..comment];
            Directive(name, condition.Trim());
        }

        private void SkipLine()
        {
            while (i < s.Length && s[i] != '\n') i++;
        }

        private void SkipBlockComment()
        {
            i += 2;
            while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
            {
                if (s[i] == '\n') line++;
                i++;
            }
            i = Math.Min(s.Length, i + 2);
        }

        private bool IsStringStart(char c, char next)
        {
            if (c == '"') return true;
            if (c is not ('@' or '$')) return false;
            var k = i;
            while (k < s.Length && s[k] is '@' or '$') k++;
            return k < s.Length && s[k] == '"' && k - i <= 8;
        }

        private void SkipString()
        {
            var dollars = 0;
            var verbatim = false;
            while (i < s.Length && s[i] is '@' or '$')
            {
                if (s[i] == '$') dollars++;
                else verbatim = true;
                i++;
            }
            var quotes = 0;
            while (i + quotes < s.Length && s[i + quotes] == '"') quotes++;
            if (quotes >= 3)
            {
                i += quotes;
                SkipRawString(quotes, dollars);
                return;
            }
            if (quotes == 2 && !verbatim && dollars == 0)
            {
                i += 2;
                return;
            }
            i++;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\n')
                {
                    if (!verbatim) return;
                    line++;
                    i++;
                    continue;
                }
                if (!verbatim && c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '"')
                {
                    if (verbatim && i + 1 < s.Length && s[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    i++;
                    return;
                }
                if (dollars > 0 && c == '{')
                {
                    if (i + 1 < s.Length && s[i + 1] == '{')
                    {
                        i += 2;
                        continue;
                    }
                    i++;
                    SkipHole(1);
                    continue;
                }
                i++;
            }
        }

        private void SkipRawString(int quotes, int dollars)
        {
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\n')
                {
                    line++;
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    var run = 0;
                    while (i + run < s.Length && s[i + run] == '"') run++;
                    i += run;
                    if (run >= quotes) return;
                    continue;
                }
                if (dollars > 0 && c == '{')
                {
                    var run = 0;
                    while (i + run < s.Length && s[i + run] == '{') run++;
                    i += run;
                    if (run >= dollars) SkipHole(dollars);
                    continue;
                }
                i++;
            }
        }

        private void SkipHole(int closing)
        {
            var depth = 0;
            while (i < s.Length)
            {
                var c = s[i];
                var next = i + 1 < s.Length ? s[i + 1] : '\0';
                if (c == '\n')
                {
                    line++;
                    i++;
                    continue;
                }
                if (c == '/' && next == '/')
                {
                    SkipLine();
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    SkipBlockComment();
                    continue;
                }
                if (IsStringStart(c, next))
                {
                    SkipString();
                    continue;
                }
                if (c == '\'')
                {
                    SkipChar();
                    continue;
                }
                if (c == '{')
                {
                    depth++;
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    if (depth > 0)
                    {
                        depth--;
                        i++;
                        continue;
                    }
                    var run = 0;
                    while (i + run < s.Length && s[i + run] == '}' && run < closing) run++;
                    i += Math.Max(1, run);
                    return;
                }
                i++;
            }
        }

        private void SkipChar()
        {
            i++;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\n') return;
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                i++;
                if (c == '\'') return;
            }
        }

        private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

        private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.ConnectorPunctuation or System.Globalization.UnicodeCategory.Format;
    }
}
