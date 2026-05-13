const state = {
  user: null,
  interns: [],
  selectedInternId: null,
  selectedIntern: null,
  editingInternId: null,
  profileCompleted: true,
  todayAttendance: null,
  locationPingTimer: null,
  punchOutRefreshTimer: null,
  sidebarCollapsed: false
};

const $ = (id) => document.getElementById(id);

const api = async (url, options = {}) => {
  const isFormData = options.body instanceof FormData;
  const response = await fetch(url, {
    credentials: 'include',
    headers: { ...(isFormData ? {} : { 'Content-Type': 'application/json' }), ...(options.headers || {}) },
    ...options
  });

  // Minimal APIs may return 204 No Content (e.g. Ok(null)) which would otherwise
  // come back as a Response object and break callers expecting a JSON value.
  if (response.status === 204) {
    return null;
  }

  if (!response.ok) {
    let message = 'Request failed.';
    try {
      const body = await response.json();
      message = body.message || message;
    } catch {
      message = response.status === 401 ? 'Invalid login or session expired.' : message;
    }
    throw new Error(message);
  }

  if (response.headers.get('content-type')?.includes('application/json')) {
    return response.json();
  }

  return response;
};

const show = (id) => {
  document.querySelectorAll('.page').forEach((page) => page.classList.add('hidden'));
  $(id).classList.remove('hidden');
  document.querySelectorAll('.nav button').forEach((button) => {
    button.classList.toggle('active', button.dataset.page === id);
  });
};

const badge = (text) => `<span class="badge ${text}">${text || '-'}</span>`;

const formatLocation = (lat, lng, area) => {
  const hasCoords = lat !== null && lat !== undefined && lng !== null && lng !== undefined;
  const coords = hasCoords ? `${Number(lat).toFixed(6)}, ${Number(lng).toFixed(6)}` : '';
  const mapLink = hasCoords
    ? `<a href="https://www.google.com/maps/search/?api=1&query=${Number(lat)},${Number(lng)}" target="_blank" rel="noopener">${coords}</a>`
    : '';
  return [area, mapLink].filter(Boolean).join('<br>');
};

const monthValue = () => new Date().toISOString().slice(0, 7);

function tickClock() {
  const now = new Date();
  $('dateText').textContent = now.toLocaleDateString('en-IN', {
    weekday: 'short',
    day: '2-digit',
    month: 'short',
    year: 'numeric'
  });
  $('timeText').textContent = now.toLocaleTimeString('en-IN');
  $('bigTime').textContent = now.toLocaleTimeString('en-IN');
  $('internDate').textContent = now.toLocaleDateString('en-IN', {
    weekday: 'long',
    day: '2-digit',
    month: 'long',
    year: 'numeric'
  });
}

async function init() {
  tickClock();
  setInterval(tickClock, 1000);
  $('attendanceMonth').value = monthValue();
  $('internMonth').value = monthValue();
  $('activityMonth').value = monthValue();
  $('locationMonth').value = monthValue();

  try {
    state.user = await api('/api/auth/me');
    openApp();
  } catch {
    $('loginView').classList.remove('hidden');
    $('appView').classList.add('hidden');
  }
}

function openApp() {
  $('loginView').classList.add('hidden');
  $('appView').classList.remove('hidden');
  $('userName').textContent = state.user.name;
  $('roleLabel').textContent = state.user.role === 'Admin' ? 'Admin Panel' : 'Intern Panel';

  if (state.user.role === 'Admin') {
    $('adminNav').classList.remove('hidden');
    $('internNav').classList.add('hidden');
    show('adminDashboard');
    loadAdmin();
  } else {
    $('internNav').classList.remove('hidden');
    $('adminNav').classList.add('hidden');
    show('internDashboard');
    loadIntern();
  }
}

async function login(event) {
  event.preventDefault();
  $('loginError').textContent = '';
  try {
    state.user = await api('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({
        username: $('loginUsername').value,
        password: $('loginPassword').value
      })
    });
    openApp();
  } catch (error) {
    $('loginError').textContent = error.message;
  }
}

async function logout() {
  await api('/api/auth/logout', { method: 'POST' });
  location.reload();
}

async function loadAdmin() {
  await Promise.all([loadDashboard(), loadInterns()]);
  // pre-fill today's date as default joining date
  const today = new Date().toISOString().slice(0, 10);
  const sd = document.querySelector('[name="internshipStartDate"]');
  if (sd && !sd.value) sd.value = today;
}

async function loadDashboard() {
  const data = await api('/api/admin/dashboard');
  $('statActive').textContent = data.active;
  $('statPresent').textContent = data.present;
  $('statLate').textContent = data.late;
  $('statHalf').textContent = data.halfDay;
  $('statAbsent').textContent = data.absent;

  $('recentRows').innerHTML = data.recent.map((row) => `
    <tr>
      <td>${row.name}</td>
      <td>${row.date}</td>
      <td>${row.clockIn || '-'}</td>
      <td>${row.clockOut || '-'}</td>
      <td>${badge(row.status)}</td>
    </tr>
  `).join('');

  const total = Math.max(1, data.chart.reduce((sum, row) => sum + row.count, 0));
  $('statusChart').innerHTML = data.chart.length ? data.chart.map((row) => `
    <div class="bar">
      <strong>${row.status}</strong>
      <span class="bar-track"><span class="bar-fill" style="width:${(row.count / total) * 100}%"></span></span>
      <span>${row.count}</span>
    </div>
  `).join('') : '<p class="muted">No attendance records for this month yet.</p>';
}

async function loadInterns() {
  // Show all interns by default. New interns start as PendingProfile until they complete their profile,
  // so filtering to Active hides most entries and looks like a bug.
  state.interns = await api('/api/admin/interns');
  $('internRows').innerHTML = state.interns.map((intern) => `
    <tr>
      <td>${intern.photoPath ? `<img src="${intern.photoPath}" class="avatar" alt="${intern.fullName}">` : '<span class="avatar empty">No</span>'}</td>
      <td>${intern.fullName}</td>
      <td>${intern.officeNumber || '-'}</td>
      <td>${intern.collegeName || '-'}</td>
      <td>${intern.profileCompleted ? '<span class="badge Present">Done</span>' : '<span class="badge Pending">Pending</span>'}</td>
      <td>${badge(intern.status)}</td>
      <td>
        <button class="ghost small" onclick="selectIntern(${intern.internId})">View</button>
        <button class="ghost small" onclick="editIntern(${intern.internId})">Edit</button>
        <button class="ghost small" onclick="deleteIntern(${intern.internId})">Delete</button>
        <button class="ghost small" onclick="resetInternPassword(${intern.internId})">Password</button>
      </td>
    </tr>
  `).join('');

  $('attendanceInternSelect').innerHTML = [
    '<option value="">All interns</option>',
    ...state.interns.map((intern) => `<option value="${intern.internId}">${intern.fullName}</option>`)
  ].join('');
  $('activityInternSelect').innerHTML = [
    '<option value="">All interns</option>',
    ...state.interns.map((intern) => `<option value="${intern.internId}">${intern.fullName}</option>`)
  ].join('');
  $('locationInternSelect').innerHTML = [
    '<option value="">All interns</option>',
    ...state.interns.map((intern) => `<option value="${intern.internId}">${intern.fullName}</option>`)
  ].join('');
}

function setFormMode(mode) {
  // mode: 'create' | 'edit'
  const createFields = $('createOnlyFields');
  const editFields = $('editOnlyFields');
  const title = $('internFormTitle');
  const btn = $('internFormBtn');
  const cancelBtn = $('cancelEditBtn');
  if (mode === 'edit') {
    createFields.classList.add('hidden');
    editFields.classList.remove('hidden');
    title.textContent = 'Edit Intern';
    btn.textContent = 'Save Changes';
    cancelBtn.classList.remove('hidden');
  } else {
    createFields.classList.remove('hidden');
    editFields.classList.add('hidden');
    title.textContent = 'Add Intern';
    btn.textContent = 'Create Intern';
    cancelBtn.classList.add('hidden');
  }
}

function cancelEdit() {
  state.editingInternId = null;
  $('internForm').reset();
  setFormMode('create');
  $('credentialBox').classList.add('hidden');
}

async function createIntern(event) {
  event.preventDefault();
  const form = new FormData(event.target);
  const photo = form.get('photo');
  const payload = Object.fromEntries(form.entries());
  delete payload.photo;
  payload.internshipEndDate = payload.internshipEndDate || null;
  payload.workLatitude = payload.workLatitude ? Number(payload.workLatitude) : null;
  payload.workLongitude = payload.workLongitude ? Number(payload.workLongitude) : null;

  try {
    let created = null;
    if (state.editingInternId) {
      await api(`/api/admin/interns/${state.editingInternId}`, {
        method: 'PUT',
        body: JSON.stringify({
          ...payload,
          officeNumber: payload.officeNumber || '',
          permanentNumber: payload.permanentNumber || '',
          collegeName: payload.collegeName || '',
          streamBranch: payload.streamBranch || '',
          yearSemester: payload.yearSemester || '',
          internshipIn: payload.internshipIn || '',
          durationOfInternship: payload.durationOfInternship || '',
          status: payload.status || 'Active'
        })
      });
      $('credentialBox').classList.remove('hidden');
      $('credentialBox').innerHTML = 'Intern profile updated.';
      state.editingInternId = null;
      setFormMode('create');
    } else {
      created = await api('/api/admin/interns', {
        method: 'POST',
        body: JSON.stringify(payload)
      });
      $('credentialBox').classList.remove('hidden');
      $('credentialBox').innerHTML = `
        <strong>Intern created!</strong><br>
        Username: <code>${created.username}</code><br>
        Password: <code>${created.temporaryPassword}</code><br>
        <span class="muted">Save these — go to Credentials tab to view all passwords.</span>`;
    }
    if (created && photo && photo.size) {
      if (photo.size > 1024 * 1024) {
        alert('Intern was created, but photo was not uploaded because it is larger than 1MB.');
      } else {
        const photoForm = new FormData();
        photoForm.append('photo', photo);
        await api(`/api/admin/interns/${created.internId}/photo`, { method: 'POST', body: photoForm });
      }
    }
    event.target.reset();
    await loadInterns();
    await loadDashboard();
  } catch (error) {
    alert(error.message);
  }
}

async function resetInternPassword(id) {
  const typed = window.prompt('Enter new password for this intern, or leave blank to auto-generate.');
  if (typed === null) return;

  try {
    const result = await api(`/api/admin/interns/${id}/password`, {
      method: 'PATCH',
      body: JSON.stringify({ newPassword: typed.trim() || null })
    });
    alert(`New intern password: ${result.temporaryPassword}`);
  } catch (error) {
    alert(error.message);
  }
}

async function selectIntern(id) {
  const intern = state.interns.find((item) => item.internId === id);
  if (!intern) return;
  state.selectedIntern = intern;
  $('internPreview').classList.remove('hidden');
  $('internPreview').innerHTML = `
    <div class="intern-preview-grid">
      ${intern.photoPath ? `<img src="${intern.photoPath}" class="avatar" alt="${intern.fullName}">` : '<span class="avatar empty">No</span>'}
      <div>
        <h3>${intern.fullName}</h3>
        <dl>
          <dt>Office number</dt><dd>${intern.officeNumber || '-'}</dd>
          <dt>Permanent number</dt><dd>${intern.permanentNumber || '-'}</dd>
          <dt>College</dt><dd>${intern.collegeName || '-'}</dd>
          <dt>Stream/Branch</dt><dd>${intern.streamBranch || '-'}</dd>
          <dt>Year-Sem</dt><dd>${intern.yearSemester || '-'}</dd>
          <dt>Internship in</dt><dd>${intern.internshipIn || '-'}</dd>
          <dt>Duration</dt><dd>${intern.durationOfInternship || '-'}</dd>
        </dl>
      </div>
    </div>
  `;
}

function editIntern(id) {
  const intern = state.interns.find((item) => item.internId === id);
  if (!intern) return;
  state.editingInternId = id;
  setFormMode('edit');
  const form = $('internForm');
  // shared fields
  if (form.fullName) form.fullName.value = intern.fullName || '';
  if (form.internshipStartDate) form.internshipStartDate.value = intern.internshipStartDate || '';
  if (form.internshipEndDate) form.internshipEndDate.value = intern.internshipEndDate || '';
  if (form.workLocationName) form.workLocationName.value = intern.workLocationName || '';
  if (form.workLatitude) form.workLatitude.value = intern.workLatitude || '';
  if (form.workLongitude) form.workLongitude.value = intern.workLongitude || '';
  // edit-only fields
  if (form.officeNumber) form.officeNumber.value = intern.officeNumber || '';
  if (form.permanentNumber) form.permanentNumber.value = intern.permanentNumber || '';
  if (form.collegeName) form.collegeName.value = intern.collegeName || '';
  if (form.streamBranch) form.streamBranch.value = intern.streamBranch || '';
  if (form.yearSemester) form.yearSemester.value = intern.yearSemester || '';
  if (form.internshipIn) form.internshipIn.value = intern.internshipIn || '';
  if (form.durationOfInternship) form.durationOfInternship.value = intern.durationOfInternship || '';
  if (form.status) form.status.value = intern.status || 'Active';
  show('adminInterns');
}

async function deleteIntern(id) {
  if (!window.confirm('Delete this intern profile? This will remove attendance and uploaded data too.')) return;
  try {
    await api(`/api/admin/interns/${id}`, { method: 'DELETE' });
    await loadInterns();
    await loadDashboard();
    $('internPreview').classList.add('hidden');
  } catch (error) {
    alert(error.message);
  }
}

async function loadAttendance() {
  const id = $('attendanceInternSelect').value;
  const [year, month] = $('attendanceMonth').value.split('-');
  const url = id
    ? `/api/admin/interns/${id}/attendance?month=${Number(month)}&year=${Number(year)}`
    : `/api/admin/attendance?month=${Number(month)}&year=${Number(year)}`;
  const rows = await api(url);
  $('attendanceRows').innerHTML = rows.map(attendanceRow).join('') || '<tr><td colspan="8">No records found.</td></tr>';
}

function attendanceRow(row) {
  return `
    <tr>
      <td>${row.internName || '-'}</td>
      <td>${row.date}</td>
      <td>${row.clockIn || '-'}</td>
      <td>${row.clockOut || '-'}</td>
      <td>${row.workingHours || '-'}</td>
      <td>${badge(row.status)}</td>
      <td>${formatLocation(row.clockInLatitude, row.clockInLongitude, row.clockInAreaName) || '-'}</td>
      <td>${formatLocation(row.clockOutLatitude, row.clockOutLongitude, row.clockOutAreaName) || '-'}</td>
    </tr>
  `;
}

function exportAttendance() {
  const id = $('attendanceInternSelect').value;
  const [year, month] = $('attendanceMonth').value.split('-');
  const query = new URLSearchParams({ month: Number(month), year: Number(year) });
  if (id) query.set('internId', id);
  window.location.href = `/api/admin/attendance/export?${query}`;
}

async function loadIntern() {
  const profile = await api('/api/intern/profile');
  state.profileCompleted = Boolean(profile.profileCompleted);
  $('userName').textContent = profile.fullName;
  if (profile.photoPath) {
    $('internPhoto').src = profile.photoPath;
    $('internPhoto').classList.remove('hidden');
  }
  if (!state.profileCompleted) {
    $('internProfileGate').classList.remove('hidden');
    $('attendanceGateContent').classList.add('hidden');
    $('todayStatus').textContent = 'Complete your profile first.';
  } else {
    $('internProfileGate').classList.add('hidden');
    $('attendanceGateContent').classList.remove('hidden');
    await loadToday();
  }
  await loadInternHistory();
  await loadInternActivities();
}

async function loadToday() {
  if (state.punchOutRefreshTimer) {
    clearTimeout(state.punchOutRefreshTimer);
    state.punchOutRefreshTimer = null;
  }
  state.todayAttendance = await api('/api/intern/attendance/today');
  const punchIn = $('punchInBtn');
  const punchOut = $('punchOutBtn');
  const statusLight = $('attendanceStatusLight');
  if (!state.todayAttendance) {
    $('todayStatus').textContent = 'Ready for manual punch in with auto location.';
    statusLight.className = 'attendance-light waiting';
    statusLight.textContent = 'Not punched in';
    punchIn.disabled = false;
    punchIn.classList.add('ready');
    punchOut.classList.remove('ready');
    punchOut.disabled = true;
    return;
  }

  const row = state.todayAttendance;
  if (!row.clockIn) {
    $('todayStatus').textContent = 'Ready for manual punch in with auto location.';
    statusLight.className = 'attendance-light waiting';
    statusLight.textContent = 'Not punched in';
    punchIn.disabled = false;
    punchIn.classList.add('ready');
    punchOut.disabled = true;
    punchOut.classList.remove('ready');
    return;
  }

  $('todayStatus').textContent = `${row.status} | In: ${row.clockIn || '-'} | Out: ${row.clockOut || '-'}`;
  if (row.clockIn && !row.clockOut) {
    statusLight.className = 'attendance-light active';
    statusLight.textContent = 'Punched in';
    punchIn.disabled = true;
    punchIn.classList.remove('ready');
    if (row.canPunchOut) {
      punchOut.disabled = false;
      punchOut.classList.add('ready');
    } else {
      punchOut.disabled = true;
      punchOut.classList.remove('ready');
      $('todayStatus').textContent = `${row.status} | In: ${row.clockIn || '-'} | Punch out opens at ${row.punchOutAvailableAt || 'after 2 hours'}`;
      schedulePunchOutRefresh(row.punchOutAvailableAt);
    }
    return;
  }

  statusLight.className = 'attendance-light completed';
  statusLight.textContent = 'Attendance marked';
  punchIn.disabled = true;
  punchOut.disabled = true;
  punchIn.classList.remove('ready');
  punchOut.classList.remove('ready');
}

async function submitAttendance(action) {
  const button = action === 'out' ? $('punchOutBtn') : $('punchInBtn');
  const originalText = button.textContent;
  const locationName = $('areaName').value.trim();
  if (!locationName) {
    alert('Enter location name, for example Head office or site name.');
    $('areaName').focus();
    return;
  }
  button.disabled = true;
  button.textContent = 'Getting location...';

  try {
    const locationData = await getLocation();
    const endpoint = action === 'out'
      ? '/api/intern/attendance/clock-out'
      : '/api/intern/attendance/clock-in';
    state.todayAttendance = await api(endpoint, {
      method: 'POST',
      body: JSON.stringify(locationData)
    });
    await sendLocationPing('attendance', locationData);
    await loadToday();
    await loadInternHistory();
    if (state.user?.role === 'Admin') await loadAttendance();
  } catch (error) {
    alert(error.message);
    await loadToday();
  } finally {
    button.textContent = originalText;
  }
}

async function sendLocationPing(source = 'manual', capturedLocation = null) {
  const locationData = capturedLocation || await getLocation();
  const ping = await api('/api/intern/location/ping', {
    method: 'POST',
    body: JSON.stringify({
      ...locationData,
      source
    })
  });
  $('lastPingStatus').textContent = `Last ping: ${ping.loggedAt} | ${ping.areaName || 'Area auto-detected'}`;
}

function getLocation() {
  return new Promise((resolve, reject) => {
    if (!navigator.geolocation) {
      reject(new Error('Location is not supported in this browser.'));
      return;
    }

    navigator.geolocation.getCurrentPosition((position) => {
      const payload = {
        latitude: position.coords.latitude,
        longitude: position.coords.longitude,
        accuracyMeters: position.coords.accuracy,
        areaName: $('areaName').value.trim()
      };
      $('locationReadout').innerHTML = `Captured: ${payload.latitude.toFixed(6)}, ${payload.longitude.toFixed(6)} | Accuracy: ${Math.round(payload.accuracyMeters)} meters`;
      resolve(payload);
    }, () => {
      reject(new Error('Location permission is required to mark attendance.'));
    }, {
      enableHighAccuracy: true,
      timeout: 15000,
      maximumAge: 0
    });
  });
}

function schedulePunchOutRefresh(timeText) {
  if (!timeText) return;
  const match = timeText.match(/^(\d{1,2}):(\d{2})\s(AM|PM)$/i);
  if (!match) return;
  const now = new Date();
  let hours = Number(match[1]) % 12;
  if (match[3].toUpperCase() === 'PM') hours += 12;
  const availableAt = new Date(now);
  availableAt.setHours(hours, Number(match[2]), 0, 0);
  const delay = availableAt.getTime() - now.getTime();
  if (delay > 0 && delay < 2.5 * 60 * 60 * 1000) {
    state.punchOutRefreshTimer = setTimeout(loadToday, delay + 1000);
  }
}

function buildInternProfilePayload(form) {
  const data = Object.fromEntries(new FormData(form).entries());
  const officeLocalNumber = (data.officeLocalNumber || '').replace(/\D/g, '');
  const personalLocalNumber = (data.personalLocalNumber || '').replace(/\D/g, '');
  if (officeLocalNumber.length !== 10 || personalLocalNumber.length !== 10) {
    throw new Error('Office and personal numbers must be exactly 10 digits.');
  }
  if (!/^[A-Za-z][A-Za-z\s.&'-]{1,119}$/.test(data.collegeName || '')) {
    throw new Error('College name must use letters.');
  }
  if (!/^[A-Za-z][A-Za-z\s.&'-]{1,79}$/.test(data.internshipIn || '')) {
    throw new Error('Internship in must use letters.');
  }
  if (!data.profileYear || !data.profileSemester) {
    throw new Error('Select both year and semester.');
  }
  return {
    officeNumber: `${data.officeCountryCode} ${officeLocalNumber}`,
    permanentNumber: `${data.personalCountryCode} ${personalLocalNumber}`,
    collegeName: data.collegeName.trim(),
    streamBranch: data.streamBranch.trim(),
    yearSemester: `${data.profileYear} - ${data.profileSemester}`,
    internshipIn: data.internshipIn.trim(),
    durationOfInternship: data.durationOfInternship
  };
}

async function loadInternHistory() {
  const [year, month] = $('internMonth').value.split('-');
  const rows = await api(`/api/intern/attendance/monthly?month=${Number(month)}&year=${Number(year)}`);
  $('internHistoryRows').innerHTML = rows.map((row) => `
    <tr>
      <td>${row.date}</td>
      <td>${row.clockIn || '-'}</td>
      <td>${row.clockOut || '-'}</td>
      <td>${row.workingHours || '-'}</td>
      <td>${badge(row.status)}</td>
      <td>${formatLocation(row.clockInLatitude, row.clockInLongitude, row.clockInAreaName) || '-'}</td>
    </tr>
  `).join('') || '<tr><td colspan="6">No records found.</td></tr>';
}

async function submitActivity(event) {
  event.preventDefault();
  const form = new FormData();
  form.append('comment', $('activityComment').value.trim());
  const file = $('activityFile').files[0];
  if (file) {
    if (file.size > 5 * 1024 * 1024) {
      alert('Upload must be 5MB or less.');
      return;
    }
    form.append('file', file);
  }

  try {
    await api('/api/intern/activities', { method: 'POST', body: form });
    $('activityMessage').classList.remove('hidden');
    $('activityMessage').textContent = 'Daily work activity submitted.';
    $('activityForm').reset();
    await loadInternActivities();
  } catch (error) {
    alert(error.message);
  }
}

async function loadInternActivities() {
  const [year, month] = $('internMonth').value.split('-');
  const rows = await api(`/api/intern/activities?month=${Number(month)}&year=${Number(year)}`);
  const existing = document.getElementById('internActivityRows');
  if (existing) {
    existing.innerHTML = rows.map(activityRow).join('') || '<tr><td colspan="6">No work activities found.</td></tr>';
  }
}

async function loadAdminActivities() {
  const id = $('activityInternSelect').value;
  const [year, month] = $('activityMonth').value.split('-');
  const query = new URLSearchParams({ month: Number(month), year: Number(year) });
  if (id) query.set('internId', id);
  const rows = await api(`/api/admin/activities?${query}`);
  $('activityRows').innerHTML = rows.map(activityRow).join('') || '<tr><td colspan="6">No work activities found.</td></tr>';
}

async function loadLocationLogs() {
  const id = $('locationInternSelect').value;
  const [year, month] = $('locationMonth').value.split('-');
  const query = new URLSearchParams({ month: Number(month), year: Number(year) });
  if (id) query.set('internId', id);
  const rows = await api(`/api/admin/location-logs?${query}`);
  $('locationRows').innerHTML = rows.map((row) => `
    <tr>
      <td>${row.logDate}</td>
      <td>${row.loggedAt}</td>
      <td>${row.internName}</td>
      <td>${row.source || '-'}</td>
      <td>${formatLocation(row.latitude, row.longitude, row.areaName) || '-'}</td>
      <td>${row.accuracyMeters ? `${Math.round(row.accuracyMeters)} m` : '-'}</td>
    </tr>
  `).join('') || '<tr><td colspan="6">No location logs found.</td></tr>';
}

function activityRow(row) {
  return `
    <tr>
      <td>${row.activityDate}</td>
      <td>${row.internName}</td>
      <td>${row.projectName || '-'}</td>
      <td>${row.comment || '-'}</td>
      <td>${row.filePath ? `<a href="${row.filePath}" target="_blank" rel="noreferrer">${row.fileName || 'Open file'}</a>` : '-'}</td>
      <td>${row.submittedAt}</td>
    </tr>
  `;
}

async function loadCredentials() {
  const rows = await api('/api/admin/credentials');
  $('credentialRows').innerHTML = rows.map((r) => `
    <tr>
      <td>${r.displayName}</td>
      <td><code>${r.username}</code></td>
      <td><code class="plain-pw">${r.password}</code></td>
      <td>${badge(r.role)}</td>
      <td>${r.isActive ? '<span class="badge Active">Yes</span>' : '<span class="badge Absent">No</span>'}</td>
      <td>${r.lastLogin || '-'}</td>
    </tr>
  `).join('');

  // populate intern select in progress section
  const internOptions = rows
    .filter((r) => r.role === 'Intern')
    .map((r) => {
      const internId = state.interns.find((i) => i.fullName === r.displayName)?.internId;
      return internId ? `<option value="${internId}">${r.displayName}</option>` : '';
    }).join('');
  $('progressInternSelect').innerHTML = '<option value="">Select an intern</option>' + internOptions;
}

async function loadProgress() {
  const id = $('progressInternSelect').value;
  const container = $('progressChartContainer');
  if (!id) { container.innerHTML = '<p class="muted">Select an intern above to view their progress.</p>'; return; }

  const p = await api(`/api/admin/interns/${id}/progress`);
  const pct = p.progressPct;
  const color = pct >= 100 ? '#80b744' : pct >= 60 ? '#14679d' : '#f59e0b';
  const daysLeft = p.remainingDays > 0 ? `${p.remainingDays} days remaining` : 'Completed';

  container.innerHTML = `
    <div class="progress-card">
      <div class="progress-header">
        <div>
          <h4>${p.fullName}</h4>
          <span class="muted">${p.startDate} → ${p.endDate || 'ongoing (90-day default)'}</span>
        </div>
        <span class="progress-pct" style="color:${color}">${pct}%</span>
      </div>
      <div class="progress-track">
        <div class="progress-fill" style="width:${pct}%;background:${color}"></div>
      </div>
      <div class="progress-meta">
        <span><strong>${p.elapsedDays}</strong> days elapsed of <strong>${p.totalDays}</strong></span>
        <span style="color:${color};font-weight:800">${daysLeft}</span>
      </div>
      <div class="progress-stats">
        <div class="pstat green"><strong>${p.presentDays}</strong><span>Present</span></div>
        <div class="pstat orange"><strong>${p.halfDays}</strong><span>Half Days</span></div>
        <div class="pstat red"><strong>${p.absentDays}</strong><span>Absent</span></div>
      </div>
      <div class="progress-days-row">
        ${Array.from({ length: p.totalDays }, (_, i) => {
          const filled = i < p.elapsedDays;
          return `<span class="day-dot ${filled ? 'filled' : ''}" title="Day ${i + 1}"></span>`;
        }).join('')}
      </div>
    </div>
  `;
}

document.addEventListener('click', (event) => {
  const page = event.target.dataset?.page;
  if (page) {
    show(page);
    if (page === 'adminActivities') loadAdminActivities();
    if (page === 'adminCredentials') loadCredentials();
    if (page === 'adminLocationLogs') loadLocationLogs();
    if (page === 'internActivity') loadInternActivities();
  }
});

$('loginForm').addEventListener('submit', login);
$('logoutBtn').addEventListener('click', logout);
$('internForm').addEventListener('submit', createIntern);
$('refreshInterns').addEventListener('click', loadInterns);
$('attendanceInternSelect').addEventListener('change', loadAttendance);
$('attendanceMonth').addEventListener('change', loadAttendance);
$('exportBtn').addEventListener('click', exportAttendance);
$('punchInBtn').addEventListener('click', () => submitAttendance('in'));
$('punchOutBtn').addEventListener('click', () => submitAttendance('out'));
$('sidebarToggle').addEventListener('click', () => {
  state.sidebarCollapsed = !state.sidebarCollapsed;
  $('appView').classList.toggle('nav-collapsed', state.sidebarCollapsed);
  $('sidebarToggle').setAttribute('aria-expanded', String(!state.sidebarCollapsed));
});
$('internMonth').addEventListener('change', async () => {
  await loadInternHistory();
  await loadInternActivities();
});
$('activityForm').addEventListener('submit', submitActivity);
$('activityInternSelect').addEventListener('change', loadAdminActivities);
$('activityMonth').addEventListener('change', loadAdminActivities);
$('locationInternSelect').addEventListener('change', loadLocationLogs);
$('locationMonth').addEventListener('change', loadLocationLogs);
$('refreshLocationLogs').addEventListener('click', loadLocationLogs);
$('internProfileForm')?.addEventListener('submit', async (event) => {
  event.preventDefault();
  try {
    const payload = buildInternProfilePayload(event.target);
    await api('/api/intern/profile', {
      method: 'PUT',
      body: JSON.stringify(payload)
    });
    alert('Profile saved. You can now mark attendance.');
    await loadIntern();
  } catch (error) {
    alert(error.message);
  }
});

init();
