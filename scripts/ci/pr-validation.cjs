// PR test evidence never authorizes deploying PR artifacts. Release payloads are
// still compiled and packaged by a direct, current-main workflow.
const fs = require('node:fs');
const { execFileSync } = require('node:child_process');

const REPOSITORY = 'Duloc-Dungeon/DildoVander';
const PROJECTS = ['repository', 'backend', 'firmware', 'web', 'ios', 'android', 'ai-eval', 'site'];
const SHA = /^[a-f0-9]{40}$/;
const RECORD = 'Record complete PR validation';
const SAVE = 'Save complete PR validation';
const JOBS = {
  'Secret scan': ['Scan checkout history without logging secret values'],
  'Repository checks': ['Test artifact provenance and promotion guards'],
  'backend / build': ['Backend tests', 'Mobile game authority and recovery (offline providers)',
    'Sealed case privacy, plans and timing (offline providers)', 'Real HTTPS passkey and invitation browser journeys',
    'Mobile game browser journeys with synthetic data', 'HTTP smoke tests'],
  'firmware / host-tests': ['Device control tests', 'DV02 compiler, owner replay and bounded diagnostics'],
  'firmware / firmware-build': ['Build ESP-IDF profile'],
  'web / web': ['Run npm test', 'Run npm test --prefix ../contracts'],
  'ios / ios': ['Test native pinned LAN transport', 'Test native debug backend client and voice sessions',
    'Test secure BLE status and secret whitelist', 'Test native identity origin policy and authentication bridge',
    'Test native diagnostic export files'],
  'android / android': ['Check release verifier and native build'],
  'ai-eval / offline': ['Validate synthetic corpus and fail-closed parser'],
  'site / build': ['Validate application packaging and viewer fixtures', 'Verify shared application browser journeys'],
  'CI required': [RECORD, SAVE],
};

function requireThat(condition, message) {
  if (!condition) throw new Error(message);
}

function positive(value) {
  return Number.isSafeInteger(value) && value > 0;
}

function makeReceipt(context, selected, results, checkout) {
  const pr = context.payload.pull_request;
  requireThat(context.eventName === 'pull_request' && pr && !pr.stack
    && context.payload.action !== 'edited' && pr.base.ref === 'main' && pr.head.ref === 'development'
    && pr.base.repo?.full_name === REPOSITORY && pr.head.repo?.full_name === REPOSITORY,
  'Only complete canonical development-to-main PR validation can be recorded');
  requireThat(PROJECTS.every(name => selected[name] === 'true' && results[name]?.result === 'success')
    && results.changes?.result === 'success' && results['secret-scan']?.result === 'success',
  'Every project and the secret scan must have executed successfully');
  requireThat([context.sha, pr.base.sha, pr.head.sha, checkout.tree].every(value => SHA.test(value || ''))
    && checkout.sha === context.sha && checkout.parents.length === 2
    && checkout.parents[0] === pr.base.sha && checkout.parents[1] === pr.head.sha,
  'PR checkout must be the exact tested merge of the event base and head');
  requireThat(positive(context.runId) && positive(context.runAttempt) && positive(pr.number), 'Invalid receipt identity');
  return { schema: 1, policy: 'full-pr-v1', repository: REPOSITORY, event: 'pull_request',
    run_id: context.runId, run_attempt: context.runAttempt, pr_number: pr.number,
    base_sha: pr.base.sha, head_sha: pr.head.sha, tested_sha: checkout.sha, source_tree: checkout.tree,
    projects: [...PROJECTS] };
}

function record({ context, core }, path = 'pr-validation.json') {
  const git = (...args) => execFileSync('git', args, { encoding: 'utf8' }).trim();
  const receipt = makeReceipt({ ...context, runAttempt: Number(process.env.GITHUB_RUN_ATTEMPT) },
    JSON.parse(process.env.SELECTED_PROJECTS), JSON.parse(process.env.RESULTS), {
      sha: git('rev-parse', 'HEAD'), tree: git('rev-parse', 'HEAD^{tree}'),
      parents: git('show', '-s', '--format=%P', 'HEAD').split(' '),
    });
  fs.writeFileSync(path, JSON.stringify(receipt) + '\n');
  core.setOutput('recorded', 'true');
}

async function mainCommit({ github, context }) {
  requireThat(`${context.repo.owner}/${context.repo.repo}` === REPOSITORY
    && context.eventName === 'workflow_dispatch' && context.ref === 'refs/heads/main'
    && SHA.test(context.sha || ''), 'Validation reuse requires a direct canonical main build');
  const { data: ref } = await github.rest.git.getRef({ ...context.repo, ref: 'heads/main' });
  requireThat(ref.object.sha === context.sha, 'Main changed during validation reuse');
  const { data: commit } = await github.rest.git.getCommit({ ...context.repo, commit_sha: context.sha });
  requireThat(commit.sha === context.sha && SHA.test(commit.tree?.sha || '') && commit.parents?.length === 2,
    'A reviewed merge commit is required');
  return commit;
}

async function discover(api) {
  const { github, context } = api;
  const commit = await mainCommit(api);
  const { data: associated } = await github.rest.repos.listPullRequestsAssociatedWithCommit({
    ...context.repo, commit_sha: context.sha, per_page: 100,
  });
  // More than one page is deliberately unsupported: ambiguity costs a full test run.
  requireThat(associated.length < 100, 'Too many associated pull requests');
  const matches = associated.filter(pr => pr.merged_at && pr.merge_commit_sha === context.sha
    && pr.base.ref === 'main' && pr.head.ref === 'development'
    && pr.base.repo?.full_name === REPOSITORY && pr.head.repo?.full_name === REPOSITORY
    && pr.head.sha === commit.parents[1].sha);
  requireThat(matches.length === 1, 'No unique merged development promotion');
  const pr = matches[0];
  const { data: workflow } = await github.rest.actions.getWorkflow({ ...context.repo, workflow_id: 'ci.yml' });
  requireThat(positive(workflow.id), 'Invalid canonical CI workflow identity');
  const { data: runs } = await github.rest.actions.listWorkflowRuns({
    ...context.repo, workflow_id: workflow.id, event: 'pull_request', head_sha: pr.head.sha,
    branch: 'development', per_page: 100,
  });
  requireThat(runs.workflow_runs.length < 100, 'Too many validation runs');
  // Pick newest before checking success: a newer failure must never be hidden.
  const run = runs.workflow_runs.filter(item => item.head_sha === pr.head.sha
    && (!item.pull_requests?.length || item.pull_requests.some(itemPr => itemPr.number === pr.number)))
    .sort((a, b) => b.id - a.id)[0];
  requireThat(run && positive(run.id) && positive(run.run_attempt), 'No PR validation run');
  await validateRun(api, run.id, run.run_attempt, pr, workflow.id);
  const { data: artifacts } = await github.rest.actions.listWorkflowRunArtifacts({
    ...context.repo, run_id: run.id, per_page: 100,
  });
  requireThat(artifacts.artifacts.length < 100, 'Too many validation artifacts');
  const proofs = artifacts.artifacts.filter(item => item.name === `pr-validation-${run.run_attempt}` && !item.expired);
  requireThat(proofs.length === 1 && positive(proofs[0].id)
    && /^sha256:[a-f0-9]{64}$/.test(proofs[0].digest || ''), 'No unique immutable validation receipt');
  return { run_id: run.id, run_attempt: run.run_attempt, workflow_id: workflow.id,
    artifact_id: proofs[0].id, artifact_digest: proofs[0].digest, pr_number: pr.number,
    head_sha: pr.head.sha, base_sha: commit.parents[0].sha, source_tree: commit.tree.sha };
}

async function validateRun({ github, context }, id, attempt, pr, workflowId) {
  const { data: run } = await github.rest.actions.getWorkflowRun({ ...context.repo, run_id: id });
  requireThat(run.id === id && run.run_attempt === attempt && run.workflow_id === workflowId
    && run.event === 'pull_request' && run.head_branch === 'development' && run.head_sha === pr.head.sha
    && run.repository?.full_name === REPOSITORY && run.head_repository?.full_name === REPOSITORY
    && run.status === 'completed' && run.conclusion === 'success'
    && (!run.pull_requests?.length || run.pull_requests.some(item => item.number === pr.number)),
  'PR run identity, attempt or success changed');
  return run;
}

async function verify(api, candidate, receipt) {
  // Independently rediscover after the artifact download to catch reruns, newer
  // attempts, changed main, artifact replacement, and changed PR association.
  const current = await discover(api);
  requireThat(JSON.stringify(current) === JSON.stringify(candidate), 'Validation candidate changed');
  requireThat(receipt.schema === 1 && receipt.policy === 'full-pr-v1' && receipt.repository === REPOSITORY
    && receipt.event === 'pull_request' && receipt.run_id === current.run_id
    && receipt.run_attempt === current.run_attempt && receipt.pr_number === current.pr_number
    && receipt.head_sha === current.head_sha && receipt.base_sha === current.base_sha
    && receipt.source_tree === current.source_tree && SHA.test(receipt.tested_sha || '')
    && JSON.stringify(receipt.projects) === JSON.stringify(PROJECTS), 'Receipt is stale, partial or for a different tree');
  const { github, context } = api;
  const { data: tested } = await github.rest.git.getCommit({ ...context.repo, commit_sha: receipt.tested_sha });
  requireThat(tested.sha === receipt.tested_sha && tested.tree.sha === current.source_tree
    && tested.parents.length === 2 && tested.parents[0].sha === current.base_sha
    && tested.parents[1].sha === current.head_sha, 'Tested PR merge differs from final main');
  const { data: jobPage } = await github.rest.actions.listJobsForWorkflowRunAttempt({
    ...context.repo, run_id: current.run_id, attempt_number: current.run_attempt, per_page: 100,
  });
  const jobs = jobPage.jobs;
  requireThat(jobs.length < 100 && jobPage.total_count === jobs.length, 'Incomplete validation job list');
  for (const [name, steps] of Object.entries(JOBS)) {
    const matches = jobs.filter(job => job.name === name);
    requireThat(matches.length === 1 && matches[0].conclusion === 'success'
      && steps.every(step => matches[0].steps?.some(item => item.name === step && item.conclusion === 'success')),
    `Complete test execution not proven: ${name}`);
  }
  requireThat(JSON.stringify(await discover(api)) === JSON.stringify(current), 'Validation changed during verification');
  return true;
}

module.exports = { PROJECTS, JOBS, makeReceipt, record, discover, verify };
