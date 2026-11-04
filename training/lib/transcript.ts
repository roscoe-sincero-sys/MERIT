import type { TimedStep } from './director.ts';
import { FEATURES, ROLES, type Workflow } from './workflow.ts';

/**
 * The written transcript: every narrated line with its time, what the viewer sees and what is done on screen. With it
 * the video has a full text alternative (WCAG 1.2.1 and 1.2.3), and it doubles as a review script.
 */
export interface TranscriptInput {
  workflow: Workflow;
  steps: TimedStep[];
  /** Milliseconds added before the recording (the title card). */
  offset: number;
  intro: string;
  outro: string;
  duration: number;
  built: string;
  appVersion: string;
  /** The workflow script, relative to the repository. */
  source: string;
}

function clock(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

export function transcript(t: TranscriptInput): string {
  const w = t.workflow;
  const out: string[] = [
    `# ${w.title}`,
    '',
    `**Role:** ${ROLES[w.role]} · **Feature:** ${FEATURES[w.feature]} · **Length:** ${clock(t.duration)}`,
    '',
    w.summary,
    '',
    '*Everything shown is synthetic demonstration data. No real person, result or credential appears in this video.*',
    '',
    `**[0:00]** ${t.intro}`,
  ];
  let scene = -1;
  for (const step of t.steps) {
    if (step.scene !== scene) {
      scene = step.scene;
      out.push('', `## ${scene + 1}. ${step.sceneTitle}`);
    }
    out.push('', `**[${clock(step.start + t.offset)}]** ${step.say}`);
    for (const item of step.log) out.push(`- *${item.kind === 'action' ? 'Action' : 'On screen'}:* ${item.text}`);
  }
  out.push('', '## Recap', '', `**[${clock(t.steps.at(-1)!.end + t.offset)}]** ${t.outro}`, '');
  for (const item of w.recap) out.push(`- ${item}`);
  out.push('', '---', '', `Generated ${t.built} from \`${t.source}\` against MERIT ${t.appVersion}.`, '');
  return out.join('\n');
}
