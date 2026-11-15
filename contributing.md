1. All commits come from a human. Someone reviewed all code that gets commited to this repository. Claude, Codex, whatever, are not co-authors on your commits.
2. AI Usage is fine. See #1, AGENTS.md.
3. Code should continue in the tradition of what is here. The code here is simple and clear to those with programming knowledge. It is not maximally idiomatic C# specific shortcuts.
4. Save compatibility is non-negotiable. Nothing added should break blueprints preventing them to load. Routines should convert old formats to new ones if they have to change.
5. Use MadiLib.
6. Update the README for Players.
7. Everything in English. Sorry.


#3 Example:

Bad
```csharp
internal static bool Allowed(float year, int? now) => !Plugin.Historical.Value || now is not int y || y >= year;
```
Good
```csharp
internal static bool Allowed(float year, int? now)
{
    // Historical availability off: everything can be picked
    if (!Plugin.Historical.Value)
        return true;
    // Outside the designer: nothing is limited
    if (now == null)
        return true;
    return now.Value >= year;
}
```