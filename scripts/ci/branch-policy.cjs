// Manual quick checks must never become evidence for a main promotion.
async function verifyDevelopmentCandidate({ github, context }, { PR_NUMBER, PR_HEAD, PR_BASE }) {
  if (!/^[1-9]\d*$/.test(PR_NUMBER || '') || !Number.isSafeInteger(Number(PR_NUMBER))
      || ![PR_HEAD, PR_BASE].every(sha => /^[a-f0-9]{40}$/.test(sha || ''))) {
    throw new Error('Expected a PR number and full head/base SHA');
  }
  const { data: pr } = await github.rest.pulls.get({
    ...context.repo, pull_number: Number(PR_NUMBER),
  });
  const repository = `${context.repo.owner}/${context.repo.repo}`;
  if (context.eventName !== 'workflow_dispatch' || pr.state !== 'open'
      || pr.base.ref !== 'development' || pr.base.repo?.full_name !== repository
      || pr.head.repo?.full_name !== repository || pr.head.sha !== PR_HEAD
      || pr.base.sha !== PR_BASE || context.sha !== PR_HEAD
      || context.ref !== `refs/heads/${pr.head.ref}`) {
    throw new Error('Dispatch must match the current same-repository development PR');
  }
}

module.exports = { verifyDevelopmentCandidate };
