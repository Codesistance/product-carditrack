using CardiTrack.Mobile.Core.Chat;

namespace CardiTrack.UnitTests.Mobile;

public class ChatSuggestionGlyphTests
{
    [Theory]
    [InlineData("What's behind the current alert?", ChatSuggestionGlyph.Alert)]
    [InlineData("Show me yesterday's Daybook", ChatSuggestionGlyph.Journal)]
    [InlineData("How did they sleep last night?", ChatSuggestionGlyph.Sleep)]
    [InlineData("How active have they been this week?", ChatSuggestionGlyph.Activity)]
    [InlineData("How are they doing today?", ChatSuggestionGlyph.General)]
    [InlineData("Has anything come through yet?", ChatSuggestionGlyph.General)]
    public void AStandardQuestion_GetsItsTopicsGlyph(string suggestion, string glyph) =>
        Assert.Equal(glyph, ChatSuggestionGlyph.For(suggestion, fromHistory: false));

    [Fact]
    public void AQuestionFromHistory_IsMarkedAsHistory_WhateverItIsAbout() =>
        Assert.Equal(ChatSuggestionGlyph.History, ChatSuggestionGlyph.For("Why did Pop sleep less?", fromHistory: true));
}
