const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { PROJECTS, JOBS, makeReceipt, discover, verify } = require('./pr-validation.cjs');

const repository = 'Duloc-Dungeon/DildoVander';
const sha = value => value.repeat(40);

function fixture() {
  const pr = { number: 42, merged_at: '2026-10-28T18:00:00Z', merge_commit_sha: sha('a'),
    base: { ref: 'main', sha: sha('c'), repo: { full_name: repository } },
    head: { ref: 'development', sha: sha('d'), repo: { full_name: repository } } };
  const context = { repo: { owner: 'Duloc-Dungeon', repo: 'DildoVander' }, sha: sha('a'),
    ref: 'refs/heads/main', eventName: 'workflow_dispatch' };
  const run = { id: 200, run_attempt: 2, workflow_id: 100, event: 'pull_request', head_sha: sha('d'),
    head_branch: 'development', repository: { full_name: repository }, head_repository: { full_name: repository },
    status: 'completed', conclusion: 'success', pull_requests: [{ number: 42 }] };
  const main = { sha: sha('a'), tree: { sha: sha('b') }, parents: [{ sha: sha('c') }, { sha: sha('d') }] };
  const tested = { ...main, sha: sha('e') };
  const artifacts = [{ id: 300, name: 'pr-validation-2', expired: false, digest: 'sha256:' + 'f'.repeat(64) }];
  const jobs = Object.entries(JOBS).map(([name, steps]) => ({ name, conclusion: 'success',
    steps: steps.map(step => ({ name: step, conclusion: 'success' })) }));
  const runs = [run];
  const associated = [pr];
  const git = {
    getRef: async () => ({ data: { object: { sha: main.sha } } }),
    getCommit: async args => ({ data: args.commit_sha === sha('a') ? main : tested }),
  };
  const actions = {
    getWorkflow: async () => ({ data: { id: 100 } }),
    listWorkflowRuns: async () => ({ data: { workflow_runs: runs } }),
    getWorkflowRun: async args => ({ data: runs.find(item => item.id === args.run_id) }),
    listWorkflowRunArtifacts: async () => ({ data: { artifacts } }),
    listJobsForWorkflowRunAttempt: async args => {
      assert.equal(args.attempt_number, 2);
      return { data: { jobs, total_count: jobs.length } };
    },
  };
  const api = { context, github: { rest: { git, actions,
    repos: { listPullRequestsAssociatedWithCommit: async () => ({ data: associated }) } } } };
  const recordContext = { ...context, eventName: 'pull_request', runId: 200, runAttempt: 2, sha: sha('e'),
    payload: { action: 'synchronize', pull_request: pr } };
  const selected = Object.fromEntries(PROJECTS.map(name => [name, 'true']));
  selected.firmware = 'false';
  const results = Object.fromEntries([...PROJECTS, 'changes', 'secret-scan'].map(name => [name, { result: 'success' }]));
  results.firmware = { result: 'skipped' };
  const checkout = { sha: sha('e'), tree: sha('b'), parents: [sha('c'), sha('d')] };
  const receipt = makeReceipt(recordContext, selected, results, checkout);
  return { api, pr, context, run, runs, main, tested, artifacts, jobs, associated,
    actions, git, receipt, recordContext, selected, results, checkout };
}

test('complete exact-tree PR proof is accepted without promoting PR artifacts', async () => {
  const f = fixture();
  const candidate = await discover(f.api);
  assert.equal(candidate.artifact_id, 300);
  assert.equal(await verify(f.api, candidate, f.receipt), true);
  assert.notEqual(f.receipt.tested_sha, f.context.sha);
});

test('recording rejects metadata, forks, quick/partial CI and wrong checkouts', () => {
  for (const mutate of [
    f => { f.recordContext.eventName = 'workflow_dispatch'; },
    f => { f.recordContext.payload.action = 'edited'; },
    f => { f.pr.head.ref = 'feature'; },
    f => { f.pr.base.ref = 'development'; },
    f => { f.pr.head.repo.full_name = 'fork/repo'; },
    f => { f.pr.stack = {}; },
    f => { f.selected.ios = 'false'; },
    f => { f.selected.firmware = 'true'; },
    f => { f.results.firmware.result = 'success'; },
    f => { f.results.backend.result = 'skipped'; },
    f => { f.results['secret-scan'].result = 'failure'; },
    f => { f.checkout.parents.reverse(); },
    f => { f.checkout.sha = sha('f'); },
  ]) {
    const f = fixture();
    mutate(f);
    assert.throws(() => makeReceipt(f.recordContext, f.selected, f.results, f.checkout));
  }
});

test('invalid discovery evidence requires full tests', async t => {
  for (const mutate of [
    f => { f.context.ref = 'refs/heads/development'; },
    f => { f.context.eventName = 'pull_request'; },
    f => { f.main.sha = sha('f'); },
    f => { f.main.parents.pop(); },
    f => { f.pr.merge_commit_sha = sha('f'); },
    f => { f.pr.head.repo.full_name = 'fork/repo'; },
    f => { f.associated.push({ ...f.pr }); },
    f => { f.run.event = 'workflow_dispatch'; },
    f => { f.run.repository.full_name = 'other/repo'; },
    f => { f.run.workflow_id = 101; },
    f => { f.run.conclusion = 'failure'; },
    f => { f.run.status = 'in_progress'; },
    f => { f.run.pull_requests = [{ number: 43 }]; },
    f => { f.artifacts.length = 0; },
    f => { f.artifacts[0].expired = true; },
    f => { f.artifacts[0].name = 'pr-validation-1'; },
    f => { f.artifacts[0].digest = ''; },
    f => { f.artifacts.push({ ...f.artifacts[0], id: 301 }); },
    f => { f.runs.push({ ...f.run, id: 201, conclusion: 'failure' }); },
    f => { f.actions.getWorkflowRun = async () => ({ data: { ...f.run, run_attempt: 3 } }); },
    f => { f.actions.getWorkflowRun = async () => { throw new Error('API unavailable'); }; },
  ]) {
    await t.test(mutate.toString(), async () => {
      const f = fixture();
      mutate(f);
      await assert.rejects(discover(f.api));
    });
  }
});

test('receipt, tree, steps, artifact and attempt races require full tests', async t => {
  for (const mutate of [
    f => { f.receipt.source_tree = sha('f'); },
    f => { f.receipt.policy = 'full-pr-v1'; },
    f => { f.receipt.base_sha = sha('f'); },
    f => { f.receipt.head_sha = sha('f'); },
    f => { f.receipt.pr_number = 43; },
    f => { f.receipt.run_attempt = 1; },
    f => { f.receipt.projects.pop(); },
    f => { f.tested.tree = { sha: sha('f') }; },
    f => { f.tested.parents = [{ sha: sha('d') }, { sha: sha('c') }]; },
    f => { f.jobs.find(job => job.name === 'ios / ios').conclusion = 'skipped'; },
    f => { f.jobs.find(job => job.name === 'CI required').steps[0].conclusion = 'skipped'; },
    f => { f.jobs.find(job => job.name === 'backend / build').steps[0].conclusion = 'skipped'; },
    f => { f.jobs.pop(); },
    f => { f.artifacts[0].id = 301; },
    f => { f.run.run_attempt = 3; },
    f => { f.actions.listJobsForWorkflowRunAttempt = async () => {
      f.main.sha = sha('f'); return { data: { jobs: f.jobs, total_count: f.jobs.length } };
    }; },
    f => { f.actions.listJobsForWorkflowRunAttempt = async () => {
      f.runs.push({ ...f.run, id: 201, conclusion: 'failure' });
      return { data: { jobs: f.jobs, total_count: f.jobs.length } };
    }; },
  ]) {
    await t.test(mutate.toString(), async () => {
      const f = fixture();
      const candidate = await discover(f.api);
      mutate(f);
      await assert.rejects(verify(f.api, candidate, f.receipt));
    });
  }
});

test('merged PR API may omit association from run; the immutable receipt still binds the exact PR', async () => {
  const f = fixture();
  f.run.pull_requests = [];
  assert.equal(await verify(f.api, await discover(f.api), f.receipt), true);
});

test('workflow optimization remains default-off, with full-test fallback and fresh payload checks', () => {
  const root = path.resolve(__dirname, '../..');
  const ci = fs.readFileSync(path.join(root, '.github/workflows/ci.yml'), 'utf8');
  const selector = fs.readFileSync(path.join(root, 'scripts/ci/select_projects.py'), 'utf8');
  const projects = selector.match(/^AUTOMATIC_PROJECTS = \((.+)\)$/m)[1];
  assert.deepEqual([...projects.matchAll(/"([^"]+)"/g)].map(match => match[1]), PROJECTS);
  assert.match(ci, /CI_REUSE_VALIDATION.*steps.validation.outputs.verified/);
  assert.match(ci, /if \[ "\$CI_REUSE_VALIDATION" = true \]; then/);
  assert.match(ci, /core.notice\(`Full tests required:/);
  assert.match(ci, /name: pr-validation-\$\{\{ github.run_attempt \}\}/);
  const recorder = ci.split('      - name: Record complete PR validation\n')[1].split('        uses:')[0];
  assert.match(recorder, /github.event.pull_request.head.repo.full_name == github.repository/);
  assert.match(recorder, /github.event.pull_request.base.repo.full_name == github.repository/);
  assert.match(recorder, /github.repository == 'Duloc-Dungeon\/DildoVander'/);
  for (const file of ['kufr-backend.yml', 'kufr-web.yml', 'kufr-site.yml']) {
    const workflow = fs.readFileSync(path.join(root, '.github/workflows', file), 'utf8');
    assert.match(workflow, /reuse-validation:[\s\S]+type: boolean\n        default: false/);
    assert.match(workflow, /if: inputs.reuse-validation != true \|\| github.event_name != 'workflow_dispatch' \|\| github.ref != 'refs\/heads\/main'/);
  }
  const backend = fs.readFileSync(path.join(root, '.github/workflows/kufr-backend.yml'), 'utf8');
  const smoke = backend.split('      - name: HTTP smoke tests\n')[1].split('      - name:')[0];
  assert.doesNotMatch(smoke, /if:/);
  assert.match(ci, /gitleaks.*git --config=.*--log-opts=HEAD/);
  const site = fs.readFileSync(path.join(root, '.github/workflows/kufr-site.yml'), 'utf8');
  assert.doesNotMatch(site, /mechanika\/final|firmware\/diagnostics/);
  assert.doesNotMatch(site, /discover -s web\/deploy -p 'test_\*\.py'/);
});
