using FunnyProject.Telegram;
using Xunit;

namespace PacketBot.Core.Tests;

public class NpcTargetSelectionTests
{
    private static readonly IReadOnlyList<string> List =
        NpcTargetSelection.Build(new[] { "Streuner" }, new[] { "lordakia", "Mordon", "STREUNER" });

    [Fact]
    public void ListMergesSavedAndSeenSortedWithoutDuplicates() =>
        Assert.Equal(new[] { "lordakia", "Mordon", "Streuner" }, List);

    [Theory]
    [InlineData("1", new[] { "lordakia", "Streuner" })]          // add
    [InlineData("3", new string[0])]                              // remove
    [InlineData("1 2,3", new[] { "lordakia", "Mordon" })]        // several, mixed separators
    [InlineData("1 1", new[] { "lordakia", "Streuner" })]        // repeated number toggles once
    [InlineData("todos", new[] { "lordakia", "Mordon", "Streuner" })]
    [InlineData(" NINGUNO ", new string[0])]
    public void AppliesChanges(string argument, string[] expected)
    {
        var (selected, error) = NpcTargetSelection.Apply(List, new[] { "Streuner" }, argument);
        Assert.Null(error);
        Assert.Equal(expected, selected);
    }

    [Theory]
    [InlineData("0", "Numero no valido: 0. Usa un numero de 1 a 3 de /npcs.")]
    [InlineData("4", "Numero no valido: 4. Usa un numero de 1 a 3 de /npcs.")]
    [InlineData("1 x", "Numero no valido: x. Usa un numero de 1 a 3 de /npcs.")]
    [InlineData("", "Indica los numeros de /npcs, 'todos' o 'ninguno'. Ejemplo: /npc 1 2 3.")]
    public void RejectsInvalidArguments(string argument, string error)
    {
        var (selected, message) = NpcTargetSelection.Apply(List, new[] { "Streuner" }, argument);
        Assert.Null(selected);
        Assert.Equal(error, message);
    }

    [Fact]
    public void EmptyListExplainsWhy()
    {
        Assert.Equal("Todavia no hay NPCs vistos en el mapa.",
            NpcTargetSelection.Apply(Array.Empty<string>(), Array.Empty<string>(), "1").Error);
        Assert.Equal("Todavia no hay NPCs vistos en el mapa.",
            NpcTargetSelection.Apply(Array.Empty<string>(), Array.Empty<string>(), "todos").Error);
    }

    [Fact]
    public void Confirmation()
    {
        Assert.Equal("Objetivos de NPC Killer en 1-1: A, B. Aplicado al bot en marcha.",
            NpcTargetSelection.Confirmation("1-1", new[] { "A", "B" }, live: true));
        Assert.Equal("Objetivos de NPC Killer en 1-1: ninguno.",
            NpcTargetSelection.Confirmation("1-1", Array.Empty<string>(), live: false));
    }
}
