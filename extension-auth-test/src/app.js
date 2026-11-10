'use strict';
import * as SDK from 'azure-devops-extension-sdk';
import { unzipSync, strFromU8 } from 'fflate';

const $ = id => document.getElementById(id);
const MAX_CHANGES = 100;
let organization = '';
let project = '';
let token = '';
let selectedRepo = null;
let repositories = [];
let pullRequests = [];
let initialized = false;
let reviewing = false;
const PILOT_REPO = 'ReviewPR-AI-Test';
// PRIVATE AUTH TEST ONLY. Use a stable trusted HTTPS backend before public release.
// Configure this one URL when your temporary Cloudflare Quick Tunnel changes.
const BACKEND_ORIGIN = 'https://treasury-suzuki-supreme-packages.trycloudflare.com';
const APPROVED_REVIEW_MODES = Object.freeze({
  mock: Object.freeze({
    provider: 'mock',
    model: 'mock-positive-test'
  }),
  gemini: Object.freeze({
    provider: 'gemini',
    model: 'gemini-3.1-flash-lite'
  })
});
const RUN_POLL_INTERVAL_MS = 4000;
const MAX_POLL_DURATION_MS = 5 * 60 * 1000;
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function message(text, isError = false, id = 'reviewStatus') {
  const el = $(id);
  if (!el) return;
  el.textContent = String(text);
  el.className = `status ${isError ? 'error' : ''}`;
}
function readableError(err) {
  const msg = err && err.message ? String(err.message) : String(err);
  return msg.replace(/Bearer\s+\S+/gi, 'Bearer [redacted]').slice(0, 400);
}
function setChoices(id, options, hint) {
  const el = $(id);
  el.replaceChildren();
  const blank = new Option(hint, '');
  el.add(blank);
  for (const [value, label] of options) el.add(new Option(label, value));
  el.disabled = options.length === 0;
}
function apiUrl(path, params = {}) {
  const url = new URL(`https://dev.azure.com/${encodeURIComponent(organization)}/${encodeURIComponent(project)}/_apis/${path}`);
  url.searchParams.set('api-version', '7.1');
  for (const [key, value] of Object.entries(params)) url.searchParams.set(key, String(value));
  return url;
}
async function request(path, params = {}) {
  if (!token) throw new Error('Azure DevOps authorization is unavailable. Reload this page.');
  const response = await fetch(apiUrl(path, params).toString(), {
    method: 'GET',
    credentials: 'omit',
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' }
  });
  if (!response.ok) {
    throw new Error(`Azure DevOps API returned HTTP ${response.status}. Check repository permissions and the vso.code extension scope.`);
  }
  return response.json();
}
async function callApi(path, { method = 'GET', params = {}, body } = {}) {
  if (!token) throw new Error('Azure DevOps extension access token is unavailable.');
  const response = await fetch(apiUrl(path, params).toString(), {
    method, credentials: 'omit', redirect: 'error',
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json',
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) })
  });
  if (!response.ok) throw new Error(`Azure DevOps returned HTTP ${response.status} for ${path.split('/')[0]}. Check extension scope and pipeline permissions.`);
  return response.json();
}
function reviewControls(enabled) {
  const btn = $('runReview');
  if (btn) btn.disabled = !enabled || reviewing || !$('pullRequest').value || selectedRepo?.name !== PILOT_REPO;
}
async function loadRepositories() {
  $('refresh').disabled = true;
  $('inspect').disabled = true;
  setChoices('repository', [], 'Loading repositories…');
  setChoices('pullRequest', [], 'Select a repository first');
  message('Loading repositories…');
  try {
    const result = await request('git/repositories');
    repositories = Array.isArray(result.value) ? result.value : [];
    repositories.sort((a, b) => String(a.name).localeCompare(String(b.name)));
    setChoices('repository', repositories.map(r => [r.id, r.name]), 'Select repository');
    message(`${repositories.length} repositories available. Select one to view its active pull requests.`);
    reviewControls(false);
  } catch (err) {
    message(readableError(err), true);
  } finally {
    $('refresh').disabled = false;
  }
}
async function loadPullRequests() {
  const id = $('repository').value;
  selectedRepo = repositories.find(r => r.id === id) || null;
  pullRequests = [];
  $('inspect').disabled = true;
  reviewControls(false);
  setChoices('pullRequest', [], id ? 'Loading pull requests…' : 'Select a repository first');
  if (!selectedRepo) return;
  message('Loading active pull requests…');
  try {
    const result = await request(`git/repositories/${encodeURIComponent(id)}/pullrequests`, {
      'searchCriteria.status': 'active', '$top': 100
    });
    pullRequests = Array.isArray(result.value) ? result.value : [];
    setChoices('pullRequest', pullRequests.map(pr => [String(pr.pullRequestId), `#${pr.pullRequestId} — ${pr.title}`]), 'Select active pull request');
    message(`${pullRequests.length} active pull requests found${pullRequests.length === 100 ? ' (first 100 shown)' : ''}.`);
    reviewControls(true);
  } catch (err) {
    message(readableError(err), true);
  }
}
function textElement(tag, value, className = '') {
  const el = document.createElement(tag);
  el.textContent = value;
  if (className) el.className = className;
  return el;
}
async function inspectPullRequest() {
  const pr = pullRequests.find(item => String(item.pullRequestId) === $('pullRequest').value);
  if (!pr || !selectedRepo) return;
  $('inspect').disabled = true;
  message(`Inspecting PR #${pr.pullRequestId}…`);
  try {
    const repoId = encodeURIComponent(selectedRepo.id);
    const prId = encodeURIComponent(String(pr.pullRequestId));
    const base = `git/repositories/${repoId}/pullrequests/${prId}`;
    const [details, iterations] = await Promise.all([
      request(base), request(`${base}/iterations`)
    ]);
    const iterationList = Array.isArray(iterations.value) ? iterations.value : [];
    const latest = iterationList.at(-1);
    let changes = [];
    if (latest) {
      // These are Azure DevOps change records, not complete textual Git diffs.
      const result = await request(`${base}/iterations/${encodeURIComponent(String(latest.id))}/changes`, { '$top': MAX_CHANGES });
      changes = Array.isArray(result.changeEntries) ? result.changeEntries : [];
    }
    const region = $('reviewResults');
    region.replaceChildren();
    region.append(textElement('h3', `PR #${details.pullRequestId} — ${details.title}`));
    region.append(textElement('p', `${details.sourceRefName || ''} → ${details.targetRefName || ''}`, 'muted'));
    region.append(textElement('p', `Status: ${details.status || 'unknown'} | Iteration: ${latest?.id ?? 'none'} | Changed entries shown: ${changes.length}`, 'small'));
    if (changes.length) {
      const wrap = document.createElement('div'); wrap.className = 'table-wrap';
      const table = document.createElement('table');
      const thead = document.createElement('thead');
      const header = document.createElement('tr');
      for (const field of ['Change', 'File']) header.append(textElement('th', field));
      thead.append(header); table.append(thead);
      const body = document.createElement('tbody');
      for (const c of changes) {
        const row = document.createElement('tr');
        row.append(textElement('td', String(c.changeType || 'modified')));
        row.append(textElement('td', String(c.item?.path || '(path unavailable)')));
        body.append(row);
      }
      table.append(body);wrap.append(table);region.append(wrap);
    } else region.append(textElement('p', 'No changed entries returned for this iteration.', 'muted'));
    region.append(textElement('p', 'Read-only inspection only. Complete authenticated textual diffs and AI reviews are not yet connected.', 'muted small'));
    message(`PR #${pr.pullRequestId} inspected successfully.`);
  } catch (err) {
    message(readableError(err), true);
  } finally {
    $('inspect').disabled = !$('pullRequest').value;
  }
}
function renderValidatedFindings(result, prId, runId, sourceCommit, targetCommit, reviewMode) {
  const expected = APPROVED_REVIEW_MODES[reviewMode];
  if (!Object.prototype.hasOwnProperty.call(APPROVED_REVIEW_MODES, reviewMode)) {
    throw new Error('Unapproved review mode.');
  }
  if (result?.status !== 'completed' || result?.publishingEnabled !== false ||
      Number(result.pullRequestId) !== Number(prId) || !Array.isArray(result.findings) ||
      !Number.isInteger(result.acceptedFindings) || result.acceptedFindings !== result.findings.length ||
      result.findings.length > 100 || result.sourceCommit !== sourceCommit ||
      result.targetCommit !== targetCommit || result.organization !== organization ||
      result.project !== project || result.repository !== PILOT_REPO || result.provider !== expected.provider || result.model !== expected.model) {
    throw new Error('Run artifact failed read-only schema and PR identity checks.');
  }
  const region = $('reviewResults');
  region.replaceChildren();
  region.append(textElement('h3', `AI review findings — PR #${prId}`));
  region.append(textElement('p', `Pipeline run #${runId} · ${result.findings.length} accepted · ${result.rejectedFindings} rejected · ${expected.provider} provider · publishing disabled`, 'muted'));
  if (!result.findings.length) region.append(textElement('p', 'No validated findings.'));
  for (const finding of result.findings) {
    const card = document.createElement('div'); card.className = 'panel';
    card.append(textElement('h3', `${finding.severity} — ${finding.category}`),
      textElement('p', `${finding.filePath}:${finding.line}`),
      textElement('p', finding.description), textElement('strong', 'Suggested fix'),
      textElement('p', finding.suggestedFix));
    region.append(card);
  }
}
async function fetchReviewArtifact(pipelineId, runId) {
  // Pipeline artifacts (PublishPipelineArtifact@1) should be retrieved through
  // the Pipelines Artifacts API, not the Build Artifacts resource download URL.
  // Request a short-lived signed URL; never forward the Azure DevOps bearer
  // token to the artifact storage endpoint.
  const artifact = await callApi(`pipelines/${pipelineId}/runs/${runId}/artifacts`, {
    params: { artifactName: 'reviewpr-findings', '$expand': 'signedContent' }
  });
  if (artifact?.name !== 'reviewpr-findings') {
    throw new Error('Run did not return the expected reviewpr-findings artifact.');
  }
  const address = artifact?.signedContent?.url;
  if (!address) {
    throw new Error('Azure DevOps did not return a signed artifact URL. Check pipeline artifact availability and build-read permission.');
  }
  const parsed = new URL(address);
  if (parsed.protocol !== 'https:' || parsed.username || parsed.password || !(
      parsed.hostname === 'dev.azure.com' ||
      parsed.hostname.endsWith('.visualstudio.com') ||
      parsed.hostname.endsWith('.vsassets.io') ||
      parsed.hostname.endsWith('.vsblob.vsassets.io'))) {
    throw new Error('Azure DevOps returned an unexpected artifact download host.');
  }
  let response;
  try {
    response = await fetch(address, { method: 'GET', credentials: 'omit', redirect: 'follow',
      headers: { Accept: 'application/zip, application/octet-stream;q=0.9, */*;q=0.1' } });
  } catch (_error) {
    throw new Error('Artifact download was blocked by the browser or storage endpoint (possibly CORS). A trusted backend proxy may be required.');
  }
  if (!response.ok) throw new Error(`Artifact download returned HTTP ${response.status}.`);
  const raw = await response.arrayBuffer();
  if (!raw.byteLength) throw new Error('Artifact download returned an empty body; no ZIP data was received.');
  if (raw.byteLength > 3_000_000) throw new Error('Review result artifact exceeds the 3 MB pilot limit.');
  const bytes = new Uint8Array(raw);
  // ZIP local file or empty archive magic; fail with actionable diagnostics.
  if (!(bytes[0] === 0x50 && bytes[1] === 0x4b &&
       ((bytes[2] === 0x03 && bytes[3] === 0x04) ||
        (bytes[2] === 0x05 && bytes[3] === 0x06)))) {
    throw new Error('Artifact endpoint did not return a ZIP file. Check the response status, content type and redirects in Network; no archive was parsed.');
  }
  let files;
  try { files = unzipSync(bytes); }
  catch (_error) { throw new Error('Downloaded ZIP could not be unpacked. Verify the pipeline artifact and its format.'); }
  const entry = Object.entries(files).find(([name]) =>
    name === 'reviewpr-result.json' || name.endsWith('/reviewpr-result.json'));
  if (!entry || entry[1].length > 1_000_000) {
    throw new Error('Expected reviewpr-result.json not found, or exceeds the 1 MB pilot limit.');
  }
  try { return JSON.parse(strFromU8(entry[1])); }
  catch (_error) { throw new Error('reviewpr-result.json is not valid JSON.'); }
}

async function reviewWithPipeline() {
  if (reviewing) return;
  const prId = Number($('pullRequest').value);
  const pipelineId = Number($('pipelineId').value);
  if (!Number.isSafeInteger(pipelineId) || pipelineId <= 0) throw new Error('Enter the numeric ID of the dedicated manual-review pipeline.');
  if (!Number.isSafeInteger(prId) || prId <= 0 || !selectedRepo || selectedRepo.name !== PILOT_REPO ||
      !pullRequests.some(p => Number(p.pullRequestId) === prId)) {
    throw new Error(`Pilot accepts an active PR from ${PILOT_REPO} only.`);
  }
  if (organization !== 'reviewpr-ai-lab' || project !== 'ReviewPR-AI-Test') {
    throw new Error('This private pilot is restricted to its test organization and project.');
  }
  const reviewMode = $('reviewMode').value;
  if (!Object.prototype.hasOwnProperty.call(APPROVED_REVIEW_MODES, reviewMode)) {
    throw new Error('Unapproved AI review mode.');
  }

  reviewing = true;
  $('reviewMode').disabled = true;
  reviewControls(false);
  const button = $('runReview'); button.textContent = 'Review running…';
  try {
    const details = await request(`git/repositories/${encodeURIComponent(selectedRepo.id)}/pullrequests/${prId}`);
    if (details.status !== 'active') throw new Error('The selected PR is no longer active.');
    const sourceCommit = details?.lastMergeSourceCommit?.commitId;
    const targetCommit = details?.lastMergeTargetCommit?.commitId;
    if (!sourceCommit || !targetCommit) throw new Error('PR commit snapshot is unavailable.');
    message(`Queueing verified review for PR #${prId}…`);
    const run = await callApi(`pipelines/${pipelineId}/runs`, {
      method: 'POST',
      body: { resources: { repositories: { self: { refName: 'refs/heads/main' } } },
        templateParameters: { pullRequestId: prId, reviewMode } }
    });
    const runId = Number(run.id);
    if (!Number.isSafeInteger(runId) || runId <= 0) throw new Error('Pipeline did not return a run ID.');
    const url = `https://dev.azure.com/${encodeURIComponent(organization)}/${encodeURIComponent(project)}/_build/results?buildId=${runId}`;
    const a = $('reviewRunLink'); a.href = url; a.hidden = false; a.textContent = `Open pipeline run #${runId}`;
    const started = Date.now();
    let finished;
    while (Date.now() - started < MAX_POLL_DURATION_MS) {
      await sleep(RUN_POLL_INTERVAL_MS);
      const current = await callApi(`pipelines/${pipelineId}/runs/${runId}`);
      message(`PR #${prId}: pipeline run #${runId} — ${current.state || 'queued'}…`);
      if (current.state === 'completed') { finished = current; break; }
    }
    if (!finished) throw new Error('Review still running. Open the pipeline run link to view its status.');
    if (finished.result !== 'succeeded') throw new Error(`Pipeline run #${runId} finished: ${finished.result}. Open its logs.`);
    const latest = await request(`git/repositories/${encodeURIComponent(selectedRepo.id)}/pullrequests/${prId}`);
    if (latest?.lastMergeSourceCommit?.commitId !== sourceCommit ||
        latest?.lastMergeTargetCommit?.commitId !== targetCommit || latest.status !== 'active') {
      throw new Error('PR changed during review. Results were not displayed; start a fresh review.');
    }
    const result = await fetchReviewArtifact(pipelineId, runId);
    renderValidatedFindings(result, prId, runId, sourceCommit, targetCommit, reviewMode);
    message(`PR #${prId}: ${result.acceptedFindings} validated findings displayed. No comments posted.`);
  } catch (err) {
    message(readableError(err), true);
  } finally {
    reviewing = false; button.textContent = 'Review with AI'; $('reviewMode').disabled = false; reviewControls(true);
  }
}
function initializeUI() {
  const required = ['context','connectionStatus','repository','pullRequest','refresh','inspect','reviewStatus','reviewResults','runReview','pipelineId','reviewMode','reviewRunLink','provider','model','scope','preview','output'];
  const missing = required.filter(id => !$(id));
  if (missing.length) throw new Error(`UI is missing controls: ${missing.join(', ')}`);
  for (const button of document.querySelectorAll('.tab')) {
    button.addEventListener('click', () => {
      for (const tab of document.querySelectorAll('.tab')) tab.setAttribute('aria-selected', String(tab === button));
      for (const panel of document.querySelectorAll('.tab-panel')) panel.hidden = panel.id !== button.dataset.tab;
    });
  }
  $('repository').addEventListener('change', () => loadPullRequests().catch(err => message(readableError(err), true)));
  $('pullRequest').addEventListener('change', () => { $('inspect').disabled = !$('pullRequest').value; reviewControls(true); });
  $('inspect').addEventListener('click', () => inspectPullRequest().catch(err => message(readableError(err), true)));
  $('refresh').addEventListener('click', () => loadRepositories().catch(err => message(readableError(err), true)));
  $('runReview').addEventListener('click', () => reviewWithPipeline().catch(err => message(readableError(err), true)));
  $('preview').addEventListener('click', () => {
    const provider = $('provider').value;
    const model = $('model').value.trim();
    const scope = $('scope').value;
    $('output').textContent = JSON.stringify({provider, model: model || '(not configured)', mode: scope, credentialConfigured: false, aiReviewEnabled: false, publishingEnabled: false}, null, 2);
  });
}
// Private test-only status UI; never log, render, persist, or forward token values
// to hosts other than this fixed development backend and Azure DevOps.
function setupBackendTestControls() {
  if ($('reviewprBackendTests')) return; // prevent duplicate controls

  const root = document.createElement('section');
  root.id = 'reviewprBackendTests';
  root.setAttribute('aria-label', 'ReviewPR backend security tests');
  root.style.cssText = 'margin:16px 12px;padding:14px;border:1px solid #77808a;border-radius:8px;';

  const heading = document.createElement('h3');
  heading.textContent = 'Backend authentication tests (private development)';
  root.appendChild(heading);

  const explanatory = document.createElement('p');
  explanatory.textContent = 'These tests verify extension authentication and Azure DevOps user identity; they do not enable BYOK or repository permissions.';
  root.appendChild(explanatory);

  function addTest(label, pendingMessage, run) {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    button.style.cssText = 'margin:8px 10px 8px 0;padding:9px 12px;cursor:pointer;';

    const status = document.createElement('div');
    status.setAttribute('role', 'status');
    status.setAttribute('aria-live', 'polite');
    status.style.cssText = 'margin:4px 0 12px;overflow-wrap:anywhere;';
    status.textContent = 'Not tested';

    button.addEventListener('click', async () => {
      button.disabled = true;
      status.textContent = pendingMessage;
      try {
        status.textContent = await run();
      } catch (_err) {
        // Do not print token values or raw exceptions that could contain them.
        status.textContent = 'Request failed. Check HTTPS tunnel, CORS, and backend configuration.';
      } finally {
        button.disabled = false;
      }
    });

    root.append(button, status);
  }

  addTest('Test BYOK Authentication', 'Checking extension authentication…', async () => {
    const appToken = await SDK.getAppToken();
    const response = await fetch(`${BACKEND_ORIGIN}/api/ai/auth-check`, {
      method: 'GET',
      credentials: 'omit',
      redirect: 'error',
      cache: 'no-store',
      headers: { Authorization: `Bearer ${appToken}`, Accept: 'application/json' }
    });
    return `Authentication HTTP status: ${response.status}${response.ok ? ' — PASS' : ' — FAIL'}`;
  });

  addTest('Test User Identity', 'Verifying Azure DevOps user identity…', async () => {
    // Fetch a current delegated token; do not use the app token for Azure DevOps REST APIs.
    const [appToken, devopsToken] = await Promise.all([
      SDK.getAppToken(),
      SDK.getAccessToken()
    ]);
    const response = await fetch(`${BACKEND_ORIGIN}/api/ai/identity-check`, {
      method: 'GET',
      credentials: 'omit',
      redirect: 'error',
      cache: 'no-store',
      headers: {
        Authorization: `Bearer ${appToken}`,
        'X-ReviewPR-DevOps-Token': devopsToken,
        Accept: 'application/json'
      }
    });
    if (!response.ok) return `Identity HTTP status: ${response.status} — FAIL`;

    const result = await response.json();
    // Require the server to have resolved a real user within the current organization.
    if (result?.authenticated !== true ||
        result?.identityVerifiedBy !== 'Azure DevOps' ||
        typeof result?.userId !== 'string' || !result.userId.trim() ||
        result?.organization !== organization) {
      return 'Identity HTTP 200, but trusted user/organization verification was incomplete.';
    }
    return `Identity verified: ${organization} — PASS (repository permissions still pending)`;
  });

  // 3F-C3: Read-only authenticated personal settings smoke test.
  addTest('Test Personal BYOK Settings', 'Loading your personal settings…', async () => {
    const [appToken, devopsToken] = await Promise.all([
      SDK.getAppToken(),
      SDK.getAccessToken()
    ]);
    const response = await fetch(`${BACKEND_ORIGIN}/api/ai/settings/me`, {
      method: 'GET',
      credentials: 'omit',
      redirect: 'error',
      cache: 'no-store',
      headers: {
        Authorization: `Bearer ${appToken}`,
        'X-ReviewPR-DevOps-Token': devopsToken,
        Accept: 'application/json'
      }
    });
    if (!response.ok) return `Personal settings HTTP ${response.status} — FAIL`;
    const result = await response.json();
    if (!result || typeof result.provider !== 'string' ||
        typeof result.model !== 'string' ||
        typeof result.credentialConfigured !== 'boolean' ||
        Object.keys(result).some(key => /^(apiKey|protectedApiKey|secret|credentialValue)$/i.test(key))) {
      return 'Personal settings HTTP 200, but response schema was invalid.';
    }
    return `Personal settings HTTP 200 — PASS (provider: ${result.provider}; key configured: ${result.credentialConfigured})`;
  });

  document.body.appendChild(root);
}

// Milestone 3F-D: personal BYOK settings UI inside the existing private test extension.
// Uses backend authorization. Never persists keys in browser storage or prints tokens.
function setupPersonalByokSettingsUI() {
  if (document.getElementById('reviewprPersonalByok')) return;

  const panel = document.createElement('section');
  panel.id = 'reviewprPersonalByok';
  panel.style.cssText = 'margin:16px 12px;padding:14px;border:1px solid #77808a;border-radius:8px;';
  panel.appendChild(textElement('h3', 'Personal BYOK Settings (private pilot)'));
  panel.appendChild(textElement('p', 'Personal settings only. Repository-wide credentials and automatic publishing are not enabled. Do not use real keys until signing-secret rotation and security review are complete.'));

  function field(name, input) {
    const label = document.createElement('label');
    label.textContent = name;
    label.style.cssText = 'display:block;margin:10px 0 4px;';
    panel.append(label, input);
    return input;
  }
  function button(label) {
    const el = document.createElement('button');
    el.type = 'button';
    el.textContent = label;
    el.style.cssText = 'margin:12px 10px 8px 0;padding:8px 12px;cursor:pointer;';
    panel.appendChild(el);
    return el;
  }
  const provider = document.createElement('select');
  for (const [value, name] of [
    ['mock', 'Mock (no API key)'],
    ['openai', 'OpenAI'],
    ['gemini', 'Google Gemini'],
    ['compatible', 'OpenAI-compatible (not yet connected)'],
    ['anthropic', 'Anthropic Claude'],
    ['groq', 'Groq'],
    ['openrouter', 'OpenRouter']
  ]) provider.add(new Option(name, value));
  field('AI provider', provider);

  const model = document.createElement('input');
  model.type = 'text';
  model.maxLength = 100;
  model.value = 'mock-positive-test';
  model.placeholder = 'Model identifier';
  model.style.cssText = 'display:block;max-width:420px;width:100%;';
  field('Model', model);

  const secret = document.createElement('input');
  secret.type = 'password';
  secret.autocomplete = 'new-password';
  secret.maxLength = 4096;
  secret.placeholder = 'Leave empty to preserve a previously stored key';
  secret.style.cssText = 'display:block;max-width:420px;width:100%;';
  field('API key (never displayed again)', secret);

  const refresh = button('Load My Settings');
  const discover = button('Discover Models');
  const testConnection = button('Test Connection');
  const modelChoices = document.createElement('select');
  modelChoices.setAttribute('aria-label', 'Discovered provider models');
  modelChoices.disabled = true;
  modelChoices.add(new Option('No models loaded', ''));
  modelChoices.style.cssText = 'display:block;max-width:420px;width:100%;margin:8px 0;';
  panel.appendChild(modelChoices);

  const save = button('Save Personal Settings');
  const remove = button('Delete My Settings');
  const status = document.createElement('p');
  status.setAttribute('role', 'status');
  status.setAttribute('aria-live', 'polite');
  panel.appendChild(status);

  // The installed private extension already initializes the Azure DevOps SDK.
  async function callPersonal(method, path, data) {
    const [appToken, userToken] = await Promise.all([SDK.getAppToken(), SDK.getAccessToken()]);
    const response = await fetch(`${BACKEND_ORIGIN}${path}`, {
      method,
      credentials: 'omit',
      redirect: 'error',
      cache: 'no-store',
      headers: {
        Authorization: `Bearer ${appToken}`,
        'X-ReviewPR-DevOps-Token': userToken,
        Accept: 'application/json',
        ...(data === undefined ? {} : {'Content-Type': 'application/json'})
      },
      ...(data === undefined ? {} : {body: JSON.stringify(data)})
    });
    return {
      status: response.status,
      data: response.ok && response.status !== 204 ? await response.json() : null
    };
  }

  let writesAllowed = false;
  let keyConfigured = false;
  function lockWrites() {
    save.disabled = !writesAllowed;
    remove.disabled = !writesAllowed;
    // Demo mock model can always be discovered and tested locally.
    discover.disabled = provider.value !== 'mock' && (!writesAllowed || !keyConfigured);
    testConnection.disabled = provider.value !== 'mock' && (!writesAllowed || !keyConfigured);
  }
  lockWrites();

  async function load() {
    status.textContent = 'Loading personal settings...';
    refresh.disabled = true;
    try {
      const capability = await callPersonal('GET', '/api/ai/settings/capabilities');
      writesAllowed = capability.status === 200 && capability.data?.personalSettingsWriteEnabled === true;
      lockWrites();
      const result = await callPersonal('GET', '/api/ai/settings/me');
      if (result.status !== 200 || !result.data || typeof result.data.provider !== 'string' ||
          typeof result.data.model !== 'string' || typeof result.data.credentialConfigured !== 'boolean') {
        status.textContent = `Cannot load personal settings (HTTP ${result.status}).`;
        return;
      }
      const data = result.data;
      if ([...provider.options].some(o => o.value === data.provider)) provider.value = data.provider;
      model.value = data.model;
      secret.value = '';
      keyConfigured = data.credentialConfigured;
      lockWrites();
      status.textContent = `Settings loaded. Credential configured: ${data.credentialConfigured}. ` +
        (writesAllowed ? 'Personal writes enabled for approved pilot.' : 'Save/Delete locked by backend security gate.');
    } catch (_err) {
      status.textContent = 'Settings request failed: check HTTPS, backend and CORS.';
      writesAllowed = false;
      keyConfigured = false;
      lockWrites();
    } finally { refresh.disabled = false; }
  }

  provider.addEventListener('change', () => {
    if (provider.value === 'mock') model.value = 'mock-positive-test';
    keyConfigured = false; // Key on disk belongs to previously saved provider.
    modelChoices.replaceChildren(new Option('No models loaded', ''));
    modelChoices.disabled = true;
    lockWrites();
  });
  modelChoices.addEventListener('change', () => {
    if (modelChoices.value) model.value = modelChoices.value;
  });
  refresh.addEventListener('click', load);

  discover.addEventListener('click', async () => {
    if (discover.disabled) return;
    discover.disabled = true;
    status.textContent = 'Discovering models from the backend...';
    try {
      const result = await callPersonal('GET', `/api/ai/models/${encodeURIComponent(provider.value)}`);
      if (result.status !== 200 || !Array.isArray(result.data?.models)) {
        status.textContent = `Model discovery unavailable (HTTP ${result.status}). Save a credential first if required.`;
        return;
      }
      modelChoices.replaceChildren(new Option('Select discovered model', ''));
      for (const name of result.data.models.slice(0, 200)) {
        if (typeof name === 'string' && name.length <= 100) modelChoices.add(new Option(name, name));
      }
      modelChoices.disabled = modelChoices.options.length < 2;
      status.textContent = `${result.data.models.length} model(s) discovered.` +
        (result.data.truncated ? ' Showing first 200.' : '');
    } catch (_err) { status.textContent = 'Could not discover models. Check HTTPS and backend.'; }
    finally { lockWrites(); }
  });

  testConnection.addEventListener('click', async () => {
    if (testConnection.disabled) return;
    testConnection.disabled = true;
    status.textContent = 'Testing provider model-catalog access...';
    try {
      const result = await callPersonal('POST', '/api/ai/test-connection');
      status.textContent = result.status === 200 && result.data?.connected === true
        ? `Connection PASS (${result.data.provider}). Selected model listed: ${result.data.modelListed === true}. ` +
          'This verifies model-list access, not inference.'
        : `Connection test not verified (HTTP ${result.status}).`;
    } catch (_err) { status.textContent = 'Connection test failed. Check HTTPS and backend.'; }
    finally { lockWrites(); }
  });

  save.addEventListener('click', async () => {
    if (!writesAllowed) return;
    save.disabled = true;
    status.textContent = 'Saving...';
    try {
      // Credential only exists briefly in this request; never log or cache it.
      const result = await callPersonal('PUT', '/api/ai/settings/me', {
        provider: provider.value,
        model: model.value.trim(),
        apiKey: secret.value
      });
      secret.value = '';
      if (result.status === 200) {
        keyConfigured = result.data?.credentialConfigured === true;
        status.textContent = `Saved ${result.data?.provider || ''} settings; key configured: ${keyConfigured}.`;
      } else {
        status.textContent = `Save rejected (HTTP ${result.status}).`;
      }
    } catch (_err) {
      secret.value = '';
      status.textContent = 'Save request failed. No confirmation of persistence.';
    } finally { lockWrites(); }
  });

  remove.addEventListener('click', async () => {
    if (!writesAllowed || !window.confirm('Delete your personal BYOK settings?')) return;
    remove.disabled = true;
    status.textContent = 'Deleting...';
    try {
      const result = await callPersonal('DELETE', '/api/ai/settings/me');
      secret.value = '';
      status.textContent = result.status === 204 ? 'Personal BYOK settings deleted.' : `Delete rejected (HTTP ${result.status}).`;
      if (result.status === 204) {
        keyConfigured = false;
        await load();
      }
    } catch (_err) { status.textContent = 'Delete request failed.'; }
    finally { lockWrites(); }
  });

  const settingsTab = document.getElementById('output')?.closest('.tab-panel');
  const legacyPreview = document.getElementById('output')?.closest('.panel');
  const legacyHeading = legacyPreview?.querySelector('h2');
  if (legacyHeading) legacyHeading.textContent = 'Legacy preview (read-only)';
  if (settingsTab) settingsTab.insertBefore(panel, settingsTab.firstElementChild);
  else document.body.appendChild(panel);
  load().catch(() => { status.textContent = 'Settings initialization failed.'; });
}

async function start() {
  if (initialized) return;
  initialized = true;
  try {
    initializeUI();
    setupReviewResultImport();
    SDK.init({ loaded: false, applyTheme: false });
    await SDK.ready();

    // Acknowledge host loading before requesting repository data.
    await SDK.notifyLoadSucceeded();
    const host = SDK.getHost();
    const web = SDK.getWebContext();
    organization = host?.name || web?.account?.name || '';
    project = web?.project?.name || '';
    if (!organization || !project) throw new Error('Open ReviewPR AI inside an Azure DevOps project.');
    $('context').textContent = `${organization} / ${project}`;
    message('Azure DevOps connected. Loading repositories…', false, 'connectionStatus');
    token = await SDK.getAccessToken();
    message('Connected using signed-in Azure DevOps permissions.', false, 'connectionStatus');
    setupBackendTestControls();
    setupPersonalByokSettingsUI();
    await loadRepositories();
  } catch (err) {
    const detail = readableError(err);
    message(detail, true, 'connectionStatus');
    message(detail, true);
    // Tell Azure DevOps if initial handshake never completed.
    try { await SDK.notifyLoadFailed(detail); } catch { /* host may not be ready */ }
  }
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
else start();


function setupReviewResultImport() {
  const inspectButton = document.getElementById('inspect');
  const region = document.getElementById('reviewResults');

  if (!inspectButton || !region) {
    console.error('ReviewPR AI: Review controls are unavailable.');
    return;
  }

  if (document.getElementById('importReviewJson')) return;

  const button = document.createElement('button');
  button.id = 'importReviewJson';
  button.type = 'button';
  button.textContent = 'Import Validated Review JSON';
  button.style.marginLeft = '10px';

  const fileInput = document.createElement('input');
  fileInput.type = 'file';
  fileInput.accept = '.json,application/json';
  fileInput.hidden = true;

  inspectButton.insertAdjacentElement('afterend', button);
  button.insertAdjacentElement('afterend', fileInput);

  button.addEventListener('click', () => fileInput.click());

  fileInput.addEventListener('change', async () => {
    const file = fileInput.files?.[0];
    if (!file) return;

    try {
      if (file.size > 1024 * 1024) {
        throw new Error('JSON file exceeds the 1 MB preview limit.');
      }

      const result = JSON.parse(await file.text());

      if (
        result.status !== 'completed' ||
        !Array.isArray(result.findings) ||
        result.publishingEnabled !== false
      ) {
        throw new Error('Invalid or unsafe review result format.');
      }

      const selectedId = document.getElementById('pullRequest')?.value;

      if (!selectedId || String(result.pullRequestId) !== selectedId) {
        throw new Error(
          'Select the same pull request as the imported review result.'
        );
      }

      if (result.findings.length > 100) {
        throw new Error('Too many findings in the review result.');
      }

      const heading = (tag, value) => {
        const el = document.createElement(tag);
        el.textContent = String(value);
        return el;
      };

      region.replaceChildren();

      region.append(
        heading('h3', `Validated review findings — PR #${selectedId}`)
      );

      region.append(
        heading(
          'p',
          `Accepted: ${result.acceptedFindings} | ` +
          `Rejected: ${result.rejectedFindings} | ` +
          `Provider: ${result.provider}`
        )
      );

      if (result.findings.length === 0) {
        region.append(heading('p', 'No validated findings.'));
      }

      for (const finding of result.findings) {
        const card = document.createElement('div');
        card.className = 'card';
        card.style.cssText =
          'padding:14px;margin:12px 0;border:1px solid #526071;' +
          'border-radius:8px;overflow-wrap:anywhere;';

        card.append(
          heading(
            'h4',
            `${finding.severity} — ${finding.category}`
          ),
          heading(
            'p',
            `${finding.filePath}:${finding.line}`
          ),
          heading('p', finding.description),
          heading('strong', 'Suggested fix'),
          heading('p', finding.suggestedFix)
        );

        region.append(card);
      }

      const status = document.getElementById('reviewStatus');

      if (status) {
        status.textContent =
          `${result.findings.length} validated findings displayed. ` +
          'Imported preview only; no PR comments posted.';
      }
    } catch (error) {
      const status = document.getElementById('reviewStatus');

      if (status) {
        status.textContent =
          `Could not import review results: ${error.message}`;
      }
    } finally {
      fileInput.value = '';
    }
  });
}
