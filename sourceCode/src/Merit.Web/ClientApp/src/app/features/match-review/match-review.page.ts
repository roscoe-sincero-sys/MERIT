import { ChangeDetectionStrategy, Component, computed, ElementRef, inject, Injectable, input, OnInit, output, signal, viewChild } from '@angular/core';
import { ApiClient, problemMessage } from '../../api/api-client';
import { Agreement, MatchCandidateDto, MatchDecision, MatchQueueItemDto, MatchReviewDto } from '../../api/contracts';

/** Match review state: the queue, the record under review, the chosen candidate. Signals only; no HTTP, no DOM. */
@Injectable()
export class MatchReviewStore {
  readonly queue = signal<MatchQueueItemDto[]>([]);
  readonly total = signal(0);
  readonly escalatedOnly = signal(false);
  readonly currentId = signal<string | null>(null);
  readonly review = signal<MatchReviewDto | null>(null);
  readonly selected = signal(0);
  readonly showHistory = signal(false);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly position = computed(() => this.queue().findIndex(q => q.stagingRowId === this.currentId()));
  readonly candidate = computed<MatchCandidateDto | null>(() => this.review()?.candidates[this.selected()] ?? null);
}

const AGREEMENT_TEXT: Record<Agreement, string> = { agree: 'Agrees', close: 'Close', different: 'Different', missing: 'Missing' };

/**
 * Presentational: the incoming record beside up to five candidates, field by field, each cell marked agrees, close (with
 * why), different or missing in words as well as colour. Reports the candidate the reviewer picks.
 */
@Component({
  selector: 'merit-match-comparison',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="scroll" tabindex="0" role="region" aria-label="Incoming record compared with candidates">
      <table class="usa-table usa-table--borderless compare">
        <caption class="usa-sr-only">Incoming record and candidates, field by field</caption>
        <thead>
          <tr>
            <th scope="col">Field</th>
            <th scope="col">Incoming record</th>
            @for (c of review().candidates; track c.personId; let i = $index) {
              <th scope="col" [class.chosen]="i === selected()">
                <button type="button" class="usa-button usa-button--unstyled pick" [attr.aria-pressed]="i === selected()"
                        [attr.aria-keyshortcuts]="'' + (i + 1)" (click)="pick.emit(i)">
                  {{ i + 1 }}. {{ c.personName }}
                </button>
              </th>
            }
          </tr>
        </thead>
        <tbody>
          @for (f of fieldNames(); track f.field) {
            <tr>
              <th scope="row">{{ f.label }}</th>
              <td>{{ f.incoming ?? '—' }}</td>
              @for (c of review().candidates; track c.personId; let i = $index) {
                @let cell = fieldOf(c, f.field);
                <td [class]="'cell cell--' + (cell?.agreement ?? 'missing')" [class.chosen]="i === selected()">
                  <span class="value">{{ cell?.candidate ?? '—' }}</span>
                  <span class="tag">{{ text(cell?.agreement ?? 'missing') }}@if (cell?.note) {: {{ cell?.note }}}</span>
                </td>
              }
            </tr>
          }
          <tr>
            <th scope="row">Why suggested</th>
            <td></td>
            @for (c of review().candidates; track c.personId; let i = $index) {
              <td [class.chosen]="i === selected()">
                {{ c.explanation }}
                @for (w of c.warnings; track w) { <p class="warning"><strong>Safeguard:</strong> {{ w }}</p> }
              </td>
            }
          </tr>
          <tr>
            <th scope="row">Record</th>
            <td></td>
            @for (c of review().candidates; track c.personId; let i = $index) {
              <td [class.chosen]="i === selected()">
                {{ c.resultCount }} result(s)@if (c.lastCollectedDate) {, last {{ c.lastCollectedDate }}}
                @if (c.openCaseNumbers) { <br />Open case {{ c.openCaseNumbers }} }
                @if (c.aliases) { <br />Also known as {{ c.aliases }} }
              </td>
            }
          </tr>
        </tbody>
      </table>
    </div>
  `,
  styles: `
    .scroll { overflow-x: auto; }
    .compare { min-width: 100%; }
    .compare th, .compare td { vertical-align: top; }
    .chosen { background: #e7f2f8; }
    th.chosen { border-bottom: 3px solid #005ea2; }
    .pick { font-weight: 700; text-align: left; }
    .value { display: block; }
    .tag { display: inline-block; margin-top: .2rem; padding: 0 .35rem; border-radius: .25rem; font-size: .8rem; }
    .cell--agree .tag { background: #ecf3ec; color: #154c21; }
    .cell--close .tag { background: #faf3d1; color: #5c4400; }
    .cell--different .tag { background: #f4e3db; color: #6f3331; font-weight: 700; }
    .cell--missing .tag { background: #f0f0f0; color: #3d4551; }
    .warning { margin: .35rem 0 0; color: #6f3331; }
  `,
})
export class MatchComparisonComponent {
  readonly review = input.required<MatchReviewDto>();
  readonly selected = input(0);
  readonly pick = output<number>();

  protected readonly fieldNames = computed(() => this.review().candidates[0]?.fields ?? []);

  protected fieldOf(c: MatchCandidateDto, field: string) {
    return c.fields.find(f => f.field === field) ?? null;
  }

  protected text(a: Agreement): string {
    return AGREEMENT_TEXT[a];
  }
}

/** Presentational: the chosen candidate's place and result history, one keystroke away. */
@Component({
  selector: 'merit-candidate-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="history" [attr.aria-label]="'History for ' + candidate().personName">
      <h3>{{ candidate().personName }}: places</h3>
      @if (candidate().places.length === 0) { <p class="merit-empty">No places recorded.</p> }
      <ul class="usa-list">
        @for (p of candidate().places; track $index) {
          <li>{{ p.isPrimary ? 'Primary ' : '' }}{{ p.role }}: {{ p.label }} ({{ p.startDate }} to {{ p.endDate ?? 'now' }})</li>
        }
      </ul>
      <h3>Recent results</h3>
      @if (candidate().results.length === 0) { <p class="merit-empty">No results.</p> }
      <ul class="usa-list">
        @for (r of candidate().results; track $index) {
          <li>{{ r.collectedDate }}: {{ r.analyteCode }} {{ r.value }}@if (r.sampleType) { ({{ r.sampleType }})}@if (r.labName) {, {{ r.labName }}}</li>
        }
      </ul>
      <p><a class="usa-link" [href]="'/people/' + candidate().personId" target="_blank" rel="noopener">Open the person page in a new tab</a></p>
    </section>
  `,
  styles: `.history { border-left: 4px solid #005ea2; padding-left: 1rem; margin-top: 1rem; } h3 { font-size: 1rem; margin: .75rem 0 .25rem; }`,
})
export class CandidateHistoryComponent {
  readonly candidate = input.required<MatchCandidateDto>();
}

/**
 * Person matching review: moves through the queue of records whose identity needs a person. Keyboard first: 1-5 pick a
 * candidate, S same person, D different person, N not sure, C create as a new person, H history, J and K next and
 * previous. Every decision is made on the server; the screen only moves on.
 */
@Component({
  selector: 'merit-match-review-page',
  imports: [MatchComparisonComponent, CandidateHistoryComponent],
  providers: [MatchReviewStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(keydown)': 'onKey($event)' },
  template: `
    <h1 tabindex="-1" #heading>Person matching review</h1>
    <p class="merit-meta">{{ store.total() }} record(s) waiting. Keys: 1&ndash;5 choose a candidate, S same person, D different person,
      N not sure, C new person, H history, J next, K previous.</p>
    <div class="usa-sr-only" role="status" aria-live="polite">{{ store.message() }}</div>
    @if (store.error(); as e) { <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div> }
    @if (store.message(); as m) { <div class="usa-alert usa-alert--success usa-alert--slim"><div class="usa-alert__body"><p class="usa-alert__text">{{ m }}</p></div></div> }

    <div class="layout">
      <nav aria-label="Records to review">
        <div class="usa-checkbox">
          <input class="usa-checkbox__input" id="escalated" type="checkbox" [checked]="store.escalatedOnly()" (change)="toggleEscalated()" />
          <label class="usa-checkbox__label" for="escalated">Sent to a supervisor only</label>
        </div>
        @if (store.queue().length === 0) { <p class="merit-empty">Nothing to review.</p> }
        <ol class="queue">
          @for (q of store.queue(); track q.stagingRowId) {
            <li>
              <button type="button" class="usa-button usa-button--unstyled" [attr.aria-current]="q.stagingRowId === store.currentId() ? 'true' : null"
                      (click)="open(q.stagingRowId)">
                {{ q.patientName ?? '(no name)' }}@if (q.dob) { &middot; {{ q.dob }}}
              </button>
              @if (q.isUrgent) { <span class="badge urgent">Urgent</span> }
              @if (q.isEscalated) { <span class="badge">Supervisor</span> }
            </li>
          }
        </ol>
      </nav>

      <section aria-labelledby="record-heading">
        @if (store.review(); as r) {
          <h2 id="record-heading">Row {{ r.rowNumber }} of {{ r.fileName }} <span class="merit-meta">from {{ r.senderName }}</span></h2>
          <p>{{ r.reasonMessage }}</p>
          @if (r.isEscalated) { <p class="usa-alert usa-alert--info usa-alert--slim usa-alert__text">Sent to a supervisor: {{ r.escalationNote }}</p> }

          @if (r.candidates.length > 0) {
            <merit-match-comparison [review]="r" [selected]="store.selected()" (pick)="choose($event)" />
          } @else {
            <p class="merit-empty">No candidate persons were found for this record (or every candidate was marked a different person).</p>
            <dl class="incoming">
              @for (f of r.incoming; track f.key) { <dt>{{ f.label }}</dt><dd>{{ f.value }}</dd> }
            </dl>
          }

          <label class="usa-label" for="note">Note (optional; sent with Not sure)</label>
          <input class="usa-input" id="note" #note maxlength="500" />
          <div class="actions" role="group" aria-label="Decision">
            @if (store.candidate(); as c) {
              <button class="usa-button" type="button" aria-keyshortcuts="S" [disabled]="store.busy()" (click)="decide('same', note.value)">
                Same person as {{ c.personName }} (S)</button>
              <button class="usa-button usa-button--secondary" type="button" aria-keyshortcuts="D" [disabled]="store.busy()" (click)="decide('different', note.value)">
                Different person (D)</button>
            } @else {
              <button class="usa-button" type="button" aria-keyshortcuts="C" [disabled]="store.busy()" (click)="decide('newPerson', note.value)">
                Create as a new person (C)</button>
            }
            <button class="usa-button usa-button--outline" type="button" aria-keyshortcuts="N" [disabled]="store.busy()" (click)="decide('notSure', note.value)">
              Not sure (N)</button>
            @if (store.candidate()) {
              <button class="usa-button usa-button--unstyled" type="button" aria-keyshortcuts="H" [attr.aria-expanded]="store.showHistory()" (click)="store.showHistory.update(v => !v)">
                {{ store.showHistory() ? 'Hide' : 'Show' }} history (H)</button>
            }
          </div>
          @if (store.showHistory() && store.candidate(); as c) { <merit-candidate-history [candidate]="c" /> }
        } @else if (store.queue().length > 0) {
          <p class="merit-indicator">Loading the record&hellip;</p>
        }
      </section>
    </div>
  `,
  styles: `
    .layout { display: grid; grid-template-columns: minmax(12rem, 16rem) 1fr; gap: 1.5rem; }
    .queue { list-style: none; padding: 0; }
    .queue li { margin: .4rem 0; }
    .queue [aria-current='true'] { font-weight: 700; text-decoration: underline; }
    .badge { margin-left: .35rem; padding: 0 .35rem; border-radius: 1rem; background: #dfe1e2; font-size: .75rem; }
    .badge.urgent { background: #b50909; color: #fff; }
    .actions { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; margin-top: 1rem; }
    .incoming { display: grid; grid-template-columns: max-content 1fr; gap: .25rem 1rem; }
    .incoming dt { font-weight: 700; }
    @media (max-width: 64em) { .layout { grid-template-columns: 1fr; } }
  `,
})
export class MatchReviewPage implements OnInit {
  protected readonly store = inject(MatchReviewStore);
  private readonly api = inject(ApiClient);
  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');
  readonly escalatedParam = input<string | undefined>(undefined, { alias: 'escalated' });

  ngOnInit(): void {
    this.store.escalatedOnly.set(this.escalatedParam() === 'true');
    this.loadQueue(true);
  }

  protected toggleEscalated(): void {
    this.store.escalatedOnly.update(v => !v);
    this.loadQueue(true);
  }

  protected open(id: string): void {
    this.store.currentId.set(id);
    this.store.review.set(null);
    this.store.selected.set(0);
    this.store.showHistory.set(false);
    this.api.matchReview(id).subscribe({
      next: r => this.store.review.set(r),
      error: e => this.store.error.set(problemMessage(e)),
    });
  }

  protected choose(i: number): void {
    const count = this.store.review()?.candidates.length ?? 0;
    if (i >= 0 && i < count) {
      this.store.selected.set(i);
    }
  }

  protected decide(decision: MatchDecision, note: string): void {
    const r = this.store.review();
    if (!r || this.store.busy()) return;
    const candidate = this.store.candidate();
    if ((decision === 'same' || decision === 'different') && !candidate) return;
    this.store.busy.set(true);
    this.store.error.set(null);
    this.api.matchDecision(r.stagingRowId, { decision, personId: candidate?.personId ?? null, note: note.trim() || null }).subscribe({
      next: res => {
        this.store.busy.set(false);
        this.store.message.set(res.message);
        if (res.released) {
          this.advance();
        } else if (decision === 'notSure') {
          // The record stays held (now with a supervisor), so move past it.
          this.loadQueue(false, this.store.position() + 1);
        } else {
          this.open(r.stagingRowId);
        }
      },
      error: e => {
        this.store.busy.set(false);
        this.store.error.set(problemMessage(e));
      },
    });
  }

  protected onKey(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (event.ctrlKey || event.altKey || event.metaKey || target.closest('input, textarea, select')) return;
    const key = event.key.toLowerCase();
    const note = (document.getElementById('note') as HTMLInputElement | null)?.value ?? '';
    const handled = (() => {
      if (key >= '1' && key <= '5') { this.choose(Number(key) - 1); return true; }
      switch (key) {
        case 's': this.decide('same', note); return true;
        case 'd': this.decide('different', note); return true;
        case 'n': this.decide('notSure', note); return true;
        case 'c': if (!this.store.candidate()) { this.decide('newPerson', note); } return true;
        case 'h': this.store.showHistory.update(v => !v); return true;
        case 'j': this.step(1); return true;
        case 'k': this.step(-1); return true;
        default: return false;
      }
    })();
    if (handled) event.preventDefault();
  }

  private step(delta: number): void {
    const q = this.store.queue();
    const next = q[this.store.position() + delta];
    if (next) this.open(next.stagingRowId);
  }

  /** After a decision the record leaves the queue; open the one that took its place. */
  private advance(): void {
    const index = Math.max(this.store.position(), 0);
    this.loadQueue(false, index);
  }

  private loadQueue(openFirst: boolean, index = 0): void {
    this.api.matchQueue({ escalated: this.store.escalatedOnly(), page: 1, pageSize: 200 }).subscribe({
      next: p => {
        this.store.queue.set(p.items);
        this.store.total.set(p.totalCount);
        const pick = p.items[Math.min(index, p.items.length - 1)];
        if (pick && (openFirst || pick.stagingRowId !== this.store.currentId() || !this.store.review())) {
          this.open(pick.stagingRowId);
        } else if (!pick) {
          this.store.currentId.set(null);
          this.store.review.set(null);
        }
        if (openFirst) this.heading()?.nativeElement.focus();
      },
      error: e => this.store.error.set(problemMessage(e)),
    });
  }
}
