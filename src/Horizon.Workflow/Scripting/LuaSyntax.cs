namespace Horizon.Workflow.Scripting;

/// <summary>
/// AvalonEdit highlighting definition (XSHD) for the <c>.moon</c> Lua dialect, shared
/// by both Layer 3 heads. Kept here as plain XML (Layer 2 takes no AvalonEdit
/// dependency); a head loads it with
/// <c>HighlightingLoader.Load(reader, HighlightingManager.Instance)</c>. Colours are
/// chosen to read on the default light system theme.
/// </summary>
public static class LuaSyntax
{
    public const string Xshd = """
        <SyntaxDefinition name="Lua" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment"  foreground="#008000" />
          <Color name="String"   foreground="#A31515" />
          <Color name="Keyword"  foreground="#0000FF" fontWeight="bold" />
          <Color name="Builtin"  foreground="#795E26" />
          <Color name="Machine"  foreground="#267F99" fontWeight="bold" />
          <Color name="Number"   foreground="#098658" />

          <RuleSet ignoreCase="false">
            <Span color="Comment" multiline="true">
              <Begin>--\[\[</Begin>
              <End>\]\]</End>
            </Span>
            <Span color="Comment">
              <Begin>--</Begin>
            </Span>

            <Span color="String">
              <Begin>"</Begin>
              <End>"</End>
              <RuleSet>
                <Span begin="\\" end="." />
              </RuleSet>
            </Span>
            <Span color="String">
              <Begin>'</Begin>
              <End>'</End>
              <RuleSet>
                <Span begin="\\" end="." />
              </RuleSet>
            </Span>

            <Keywords color="Keyword">
              <Word>and</Word>   <Word>break</Word> <Word>do</Word>     <Word>else</Word>
              <Word>elseif</Word><Word>end</Word>   <Word>false</Word>  <Word>for</Word>
              <Word>function</Word><Word>goto</Word><Word>if</Word>     <Word>in</Word>
              <Word>local</Word> <Word>nil</Word>   <Word>not</Word>    <Word>or</Word>
              <Word>repeat</Word><Word>return</Word><Word>then</Word>   <Word>true</Word>
              <Word>until</Word> <Word>while</Word>
            </Keywords>

            <Keywords color="Builtin">
              <Word>print</Word>  <Word>pairs</Word>   <Word>ipairs</Word> <Word>tostring</Word>
              <Word>tonumber</Word><Word>type</Word>   <Word>pcall</Word>  <Word>error</Word>
              <Word>select</Word> <Word>string</Word>  <Word>table</Word>  <Word>math</Word>
              <Word>assert</Word>
            </Keywords>

            <Keywords color="Machine">
              <Word>machine</Word>
            </Keywords>

            <Rule color="Number">
              \b0[xX][0-9a-fA-F]+|\b\d+(\.[0-9]+)?\b
            </Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
