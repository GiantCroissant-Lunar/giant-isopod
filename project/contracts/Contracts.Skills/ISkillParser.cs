namespace GiantIsopod.Contracts.Skills;

/// <summary>
/// Parses SKILL.md files supporting both Agent Skills spec and legacy formats.
/// </summary>
public interface ISkillParser
{
    SkillDefinition? Parse(string skillMdContent, string? filePath = null);
    IReadOnlyList<SkillDefinition> ParseDirectory(string directoryPath);
}
