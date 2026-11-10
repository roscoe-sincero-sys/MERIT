# Caregiver apartment/unit correction requests (#260)

Address correction requests now record one explicit apartment/unit instruction:

- **Not part of this correction**: no claim about the unit; preserves the behavior of old requests.
- **Set or replace apartment / unit**: requires a unit, up to 30 characters.
- **No apartment / unit**: explicitly remove any current unit; leave the proposed unit blank.

The request form, owning-team inbox and history display the instruction separately from the street/city/state/ZIP. Conflicting instructions, a missing unit for set/replace, and unit instructions on phone/email requests are rejected. Address details remain open after address-form validation errors.

For set/remove instructions, the application computes the proposed address key using the same `MatchKeys.AddressKey` normalization as residence creation/correction. The key is server-controlled and is not accepted from form fields. The review writer requires the current primary residence to match that key as well as the existing street/city/state/ZIP checks. For example, a request for `Apt. 3f` can match the verified residence unit `3F`, while the old `2F` cannot satisfy it. Explicit removal requires the normalized unit component to be empty.

The owning team still applies changes through the existing caregiver residence form and records review evidence. No address is updated automatically from free-text source notes, and county contact-only access does not gain a caregiver medical-chart write path. Before/after snapshots and review history remain intact; outreach export continues to read the verified current residence, including its unit.

Existing requests receive `unitAction = unspecified`, with null proposed unit/key. Their status and original evidence are not rewritten, and they retain their previous review semantics. New requests can also deliberately leave the unit outside the correction. An empty unit field by itself never means removal.

Validation covers normalized changes, unchanged-unit rejection, explicit removal, already unit-less residences, unspecified/legacy semantics, tampered form keys, owner-only review, source history and the actual outreach CSV. Upgrade validation uses an old-schema pending request and confirms its status/snapshot remain unchanged. Browser checks cover all three instructions, validation feedback, owner handoff, accessibility and desktop/phone layouts.

Verified locally: solution/final web builds completed without warnings or errors; 333 unit/architecture tests passed (five optional mail tests skipped); 205 integration tests passed on a fresh database (one opt-in historical-clock replay skipped); all five selected Edge tests passed with axe/overflow checks at 1280/390/320px; three SQL-generator tests passed. Fresh installation and an upgrade containing a pre-existing pending request both completed successfully with the legacy request's status, snapshot and unspecified-unit semantics preserved.
