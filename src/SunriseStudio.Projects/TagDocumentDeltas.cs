using SunriseStudio.Tags.Document;

namespace SunriseStudio.Projects;

/** Turns a tag document's edits into the project delta model, so they flow through StudioProject.ApplyEdit. */
public static class TagDocumentDeltas
{
    /** @return The delta from the installed bytes to the document's current bytes. */
    public static BinaryDelta Delta(this TagDocument document) => BinaryDelta.Create(document.Original, document.Encode());
}
