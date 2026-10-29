const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { verifyDevelopmentCandidate } = require('./branch-policy.cjs');

const root = path.resolve(__dirname, '../..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');

function fixture() {
  const pr = { state: 'open', base: { ref: 'development', sha: 'b'.repeat(40),
    repo: { full_name: 'owner/repo' } },
  head: { ref: 'feature', sha: 'a'.repeat(40), repo: { full_name: 'owner/repo' } } };
  const context = { repo: { owner: 'owner', repo: 'repo' }, eventName: 'workflow_dispatch',
    ref: 'refs/heads/feature', sha: pr.head.sha };
  const inputs = { PR_NUMBER: '42', PR_HEAD: pr.head.sha, PR_BASE: pr.base.sha };
  const github = { rest: { pulls: { get: async args => {
    assert.equal(args.pull_number, 42);
    return { data: pr };
  } } } };
  return { api: { github, context }, pr, inputs };
}

test('manual quick CI accepts an exact development candidate', async () => {
  const { api, inputs } = fixture();
  await verifyDevelopmentCandidate(api, inputs);
});

test('manual quick CI rejects main, forks, stale candidates and unrelated refs', async t => {
  for (const mutate of [
    f => { f.pr.base.ref = 'main'; },
    f => { f.pr.state = 'closed'; },
    f => { f.pr.head.repo.full_name = 'fork/repo'; },
    f => { f.pr.base.repo.full_name = 'other/repo'; },
    f => { f.pr.head.sha = 'c'.repeat(40); },
    f => { f.pr.base.sha = 'c'.repeat(40); },
    f => { f.api.context.sha = 'c'.repeat(40); },
    f => { f.api.context.ref = 'refs/heads/main'; },
    f => { f.api.context.eventName = 'push'; },
    f => { f.inputs.PR_NUMBER = '0'; },
    f => { f.inputs.PR_HEAD = 'short'; },
    f => { f.inputs.PR_BASE = ''; },
  ]) {
    await t.test(mutate.toString(), async () => {
      const f = fixture();
      mutate(f);
      await assert.rejects(verifyDevelopmentCandidate(f.api, f.inputs));
    });
  }
});

test('development runs affected product checks and a distinct fail-closed gate', () => {
  const workflow = read('.github/workflows/development-ci.yml');
  assert.match(workflow, /pull_request:\n    branches: \[development\]/);
  assert.match(workflow, /name: Development required/);
  assert.match(workflow, /needs: \[candidate, secret-scan, repository, changes, backend, firmware, web, ios, android, ai-eval, site\]/);
  assert.match(workflow, /if: always\(\)/);
  assert.match(workflow, /uses: \.\/\.github\/workflows\/kufr-backend.yml/);
  assert.match(workflow, /uses: \.\/\.github\/workflows\/kufr-site.yml/);
  assert.match(workflow, /selected.get\(name\) == 'true' else 'skipped'/);
  assert.match(workflow, /verifyDevelopmentCandidate\(\{ github, context \}, process.env\)/);
  assert.match(workflow, /gitleaks.*git --config=.*--redact=100/);
  assert.match(workflow, /actionlint.*-shellcheck= -pyflakes=/);
  assert.match(workflow, /node --test scripts\/ci\/\*\.test.cjs/);
  assert.doesNotMatch(workflow, /workflow_call:|\$\{\{\s*secrets\.|: write\b|self-hosted/);
});

test('development promotion selects every existing project while release stays main-only', () => {
  const ci = read('.github/workflows/ci.yml');
  assert.match(ci, /pull_request:\n    types:.*\n    branches: \[main\]/);
  assert.match(ci, /CI_HEAD_REF: \$\{\{ github.event.pull_request.head.ref \|\| github.ref_name \}\}/);
  assert.match(ci, /if \[ "\$CI_HEAD_REF" = development \] && \[ -n "\$CI_PR_NUMBER" \]; then\n\s+python3 scripts\/ci\/select_projects.py select --project all/);
  assert.match(read('.github/workflows/main-release.yml'), /push:\n    branches: \[main\]/);
});

test('persistent development is protected and main preserves promotion ancestry', () => {
  const development = JSON.parse(read('.github/development-ruleset.json'));
  const main = JSON.parse(read('.github/main-ruleset.json'));
  const settings = JSON.parse(read('.github/repository-settings.json'));
  assert.deepEqual(development.conditions.ref_name.include, ['refs/heads/development']);
  assert.equal(development.enforcement, 'active');
  assert.deepEqual(development.bypass_actors, []);
  assert.ok(development.rules.some(rule => rule.type === 'deletion'));
  assert.ok(development.rules.some(rule => rule.type === 'non_fast_forward'));
  const gate = development.rules.find(rule => rule.type === 'required_status_checks').parameters;
  assert.deepEqual(gate.required_status_checks, [{ context: 'Development required', integration_id: 15368 }]);
  assert.equal(gate.strict_required_status_checks_policy, false);
  assert.deepEqual(main.rules.find(rule => rule.type === 'pull_request').parameters.allowed_merge_methods, ['merge']);
  assert.deepEqual(development.rules.find(rule => rule.type === 'pull_request').parameters.allowed_merge_methods, ['squash', 'merge']);
  assert.equal(settings.allow_merge_commit, true);
  assert.equal(settings.allow_squash_merge, true);
});

test('dependency changes target development and retain trusted main automation', () => {
  assert.equal((read('.github/dependabot.yml').match(/target-branch: development/g) || []).length, 3);
  const generator = read('.github/workflows/dependabot-generate.yml');
  assert.match(generator, /pull_request:\n    branches: \[development\]/);
  assert.match(generator, /github.ref == 'refs\/heads\/main'/);
  assert.match(read('scripts/ci/dependabot-sync.cjs'), /workflow_id: 'development-ci.yml'/);
});
