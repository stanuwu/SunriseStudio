/**
 * File: The in-editor IEditorGateway (UI-UE5 §5.4): how a model-workflow worker (OpenAiCompatibleWorker and the
 * subscription clients' prompt context) reaches this editor. It goes through the same ISolforgeHost the MCP tools use (the
 * form's AiHost), bound to the workflow's project: get_context is the published context snapshot, list_open_documents is
 * get_open_documents (dirty and sha256), read_document reads the whole file with its sha256, and request_document_edit is
 * apply_project_edit with expected_sha256, so the user reviews the exact before/after text in the diff modal.
 */
using Solforge.Harness.Runtime;
using Solforge.Mcp;

namespace SunriseStudio.Editor;

/** HostEditorGateway over the editor's MCP host; refuses another project, paused sharing and the credential file. */
internal sealed class EditorGateway(ISolforgeHost host, string projectRoot) : HostEditorGateway(host, projectRoot)
{
    /** The project the workflow was started for. */
    public string ProjectRoot { get; } = projectRoot;
}
