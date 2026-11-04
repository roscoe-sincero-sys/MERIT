import type { Stage } from './stage.ts';

/**
 * A training video is a script, not a recording: scenes of steps, each with the line the narrator says (also the
 * caption) and the actions that go with it. When a screen changes, edit the script and regenerate.
 */
export const ROLES = {
  allUsers: 'All users',
  intakeClerk: 'Intake clerk',
  caseManager: 'Case manager',
  investigator: 'Investigator',
  occupationalInvestigator: 'Occupational investigator',
  supervisor: 'Supervisor',
  epidemiologist: 'Epidemiologist',
  administrator: 'Administrator',
  userAdministrator: 'User administrator',
  platformAdministrator: 'Platform administrator',
} as const;

export const FEATURES = {
  signIn: 'Signing in and your account',
  navigation: 'Finding your way around',
  profiles: 'Profiles and working in',
  intake: 'Lab file intake',
  peopleAndResults: 'People and results',
  cases: 'Cases and work queues',
  investigations: 'Investigations',
  surveillance: 'Clusters, the map and reports',
  extracts: 'CBLS and ABLES extracts',
  registry: 'Reporter registry',
  access: 'Users and access',
  security: 'Sign-in security',
} as const;

export type RoleId = keyof typeof ROLES;
export type FeatureId = keyof typeof FEATURES;

export interface Step {
  /** What the narrator says, and the caption. One or two plain sentences. */
  say: string;
  /** What the step does on screen. Omit for a step that only explains what is already shown. */
  run?: (stage: Stage) => Promise<void>;
  /** Extra time to hold the result on screen, in milliseconds, after both the actions and the speech finish. */
  hold?: number;
}

export interface Scene {
  title: string;
  steps: Step[];
}

export interface Workflow {
  /** File-name-safe identifier, unique across the catalog. */
  id: string;
  title: string;
  /** One sentence: what the viewer will be able to do. Used on the title card and in the catalog. */
  summary: string;
  role: RoleId;
  feature: FeatureId;
  /** The synthetic demo account that performs the workflow. */
  user: string;
  /** The page open when recording starts. */
  start: string;
  /** Puts the training data back to the same starting point, so every build of the video is the same. */
  prepare?: () => void | Promise<void>;
  scenes: Scene[];
  /** The closing card: what the viewer just did. */
  recap: string[];
}

export function defineWorkflow(workflow: Workflow): Workflow {
  if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(workflow.id)) throw new Error(`Workflow id "${workflow.id}" must be lower-case words joined by hyphens.`);
  return workflow;
}
