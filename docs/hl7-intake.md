# Native HL7 laboratory intake

MERIT accepts ER7 text files containing **HL7 v2.3.1 or v2.5.1 ORU^R01** messages.
Choose the laboratory's **HL7** profile on **Intake** and upload a `.hl7` or `.txt` file.
The worker parses the original messages directly; no CSV conversion is required.
This is file intake, not an MLLP listener or an HL7 acknowledgment service.

## Profiles and deployment

Deploy the database update before running the new Web and Worker versions. The generated
`144_dbo.intakeProfile.sql` script widens the existing file-type constraint atomically, retaining
previously accepted types. `dbo.uspFindDuplicateResult` gains an optional observation-key parameter.
Reapplying the deployment converges without deleting data.

The Maryland demo installer adds **Chesapeake Reference Laboratory HL7** alongside the existing
CSV profile, including when upgrading an already seeded demo database. Use it with
`sourceCode/samples/chesapeake_hl7_231.hl7` and `chesapeake_hl7_251.hl7`.
These synthetic files report the same patient with distinct accessions; the 2.5.1 file contains
two observations with distinct sub-IDs. Native HL7 and delimited profiles remain separate.

As with existing intake profiles, an administrator provisions a source using
`dbo.uspSaveIntakeProfile` under an authorized session with `intake.configure`:

```sql
EXEC dbo.uspSaveIntakeProfile
    @intakeProfileId = '<new profile GUID>',
    @senderOrganizationId = '<registered source laboratory GUID>',
    @profileName = N'Laboratory HL7',
    @fileType = 'hl7',
    @columnMapJson = N'{}',
    @dateFormats = N'yyyy-MM-dd',
    @constantsJson = N'{}',
    @valueTranslationsJson = N'{}';
```

Native field mapping is fixed; CSV column mappings, header rows, and header signatures do not
apply. Value translations operate on the canonical staging fields after parsing. Constants fill
missing/empty fields, and cannot replace populated source values. An interface can, for example,
map a local test identifier such as `99LAB:LEAD` to a configured LOINC using `valueTranslationsJson`.
Dates produced by the reader always use `yyyy-MM-dd`, independent of a legacy profile's date formats.
If MSH-4 supplies a CLIA identifier, it must match the selected source laboratory's CLIA.
Non-CLIA sending-facility identifiers remain in provenance; the selected profile binds the source.
OBX-23 can supply a performing laboratory CLIA in XON component 10.

## Mapped fields

| Source | MERIT use |
| --- | --- |
| MSH-3/4/10/12 | Sending application/facility, message control ID and version in provenance |
| PID-3 repetitions | Patient identifiers with assigning authority; MR maps to MRN, MA to Medicaid, other types to other |
| PID-5 | Legal name when supplied, otherwise first name repetition; family/given/middle names |
| PID-7/8 | Birth date and sex; unknown/unsupported sex codes become unknown |
| PID-11 | Home address when supplied, otherwise first address; street, unit, city, state, ZIP and county |
| OBR-3, then OBR-2, then ORC-3 | Accession number |
| SPM-17, then OBR-7, then OBX-14 | Collection date |
| OBR-22, then MSH-7 | Report date |
| OBR-16, then ORC-12 | Ordering provider identifier, linked only when it is a valid registered NPI |
| OBX-3 | Primary or alternate LOINC (`LN`/`LOINC`); local identifiers are namespaced and require translation or review |
| OBX-5 repetitions / OBX-6 | Result values and units, one staging row per value repetition |
| SPM-4, then OBR-15 | Venous/capillary sample type when identified; configured LOINC implications also apply |
| OBX-11/17 | Result status and method (including ICPMS, GFAA, ASV, LEADCARE translations) |

Patient identifiers are saved only after a row passes clinical validation and person matching.
They are shown through the existing `person.view_identifiers` permission and are excluded from the
general result snapshot; the complete intake source and staging data remain preserved.
They do not override MERIT's identity checks or tenant scope. Repeated names and addresses remain
in the reported snapshot; additional repetitions do not create extra residences automatically.
The source file retains unmapped segments, notes, additional fields, full timestamps and coding details.
The application uses the reported calendar dates, without converting time-of-day offsets to a different date.

## Message handling and limits

- Each message declares its own separators in MSH-1/2. Components, subcomponents, repetitions and
  standard delimiter escapes are parsed before text is decoded. UTF-8 hex escapes, highlighting
  markers and `.br` are supported; unknown escapes in mapped fields cause a pause.
- CR, CRLF and LF segment endings are accepted. Concatenated messages, optional file/batch
  envelopes, and retained MLLP boundary characters in exported files are supported.
- Multiple PID and OBR groups and multiple OBX observations are supported. One SPM can precede
  or follow the observations for an OBR. Multiple SPMs under one OBR are ambiguous and pause
  intake; send separate OBR groups. Fragmented messages with DSC/ADD must be assembled first.
- NM and scalar SN results are supported. ST/TX values enter the same numeric validation as CSV;
  nonnumeric values are held rather than discarded. SN ranges/ratios and comparators other than
  `=`, `<` or `>` pause intake. Other value types, including ED attachments, require a separate mapping.
- Status F is final; P/R/S/I preliminary; C corrected; D/X cancelled. Each needs a result value
  accepted by the existing clinical validator. Missing/other statuses pause intake rather than defaulting to final.
- Character sets are ASCII or UTF-8. Other declared character sets require conversion before upload.
- The existing upload limit is 20 MB; native parsing also limits each segment to 65,536 characters
  and each file to 10,000 observation-value rows. Split larger feeds before uploading.
- Mapped result text and accessions are limited to 60 characters, units to 20, methods to 30,
  and unknown test codes/unreadable dates to 200. Larger values pause intake before SQL writes.

## Matching, duplicates and corrections

The existing matching, held-record, normalization, classification, audit and row-level security
services process native rows under the session/profile/acting tenant saved with the job.
Sender content never chooses the destination tenant. A region user should select the appropriate
state as their acting tenant before uploading that state's source file.

For native results, duplicate/correction lookup also compares a stable observation key stored
in the reported snapshot: test code, observation sub-ID and value repetition. Distinct measurements
under one accession therefore remain distinct. Repeated test codes under one OBR require distinct
OBX-4 sub-IDs; resends/corrections must retain those sub-IDs and repetition identities.
The original accession is preserved. Corrected/cancelled rows can supersede their matching result.
Delimited/manual rows retain their previous duplicate key. Native and legacy rows with different
observation identities are not automatically equated as duplicate results; person matching still
works across sources and file formats.

Malformed headers, unsupported versions/events, ambiguous structures or unsupported encoding/value
types **pause the whole batch before any rows are staged**. The Intake batch card and a paused-batch
task show a message number and a field/segment reason, without copying source values into the diagnostic.
Correct the source file and upload it again; a deliberate resend of identical content requires the
existing resend acknowledgment. Valid structure with unknown test codes/units, missing DOB or other
clinical problems follows the existing held-record workflow.

Format references: [HL7 message encoding](https://hl7.eu/HL7v2x/v251/std251/ch02.html) and
[HL7 ORU and observation structure](https://hl7.eu/HL7v2x/v251/std251/ch07.html).
