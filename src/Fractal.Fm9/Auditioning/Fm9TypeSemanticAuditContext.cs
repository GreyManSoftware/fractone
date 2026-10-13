namespace Fractal.Companion.Auditioning
{
	// A setting held at a fixed ordinal while a block type is enumerated, recorded with the audit evidence.
	public sealed record Fm9TypeSemanticAuditContext(
		string ParameterName,
		int ParameterId,
		int Ordinal);
}
