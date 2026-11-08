using Merit.Application.Data;
using Merit.Application.Services;
namespace Merit.Web.Models;

public sealed record WorkflowConfigurationModel(WorkflowDefinitionSave Draft, IReadOnlyList<WorkflowDefinitionRow> Definitions,
    IReadOnlyList<WorkflowPreviewRow> Previews, IReadOnlyList<SelectOption> Specimens, Guid? SamplePersonId, Guid? SecondSamplePersonId, Guid? SampleCaseId, Guid? SecondSampleCaseId);
public sealed record PatientWorkModel(PersonPage Person, IReadOnlyList<PatientWorkRow> Work);
public sealed record TaskEvidenceModel(PatientWorkRow Task, PersonPage Person);
public sealed record ActivityCorrectionModel(Guid PersonId, Guid ActivityId, string SourceKind, string Reason);
