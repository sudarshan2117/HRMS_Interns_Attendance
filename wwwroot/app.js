const state = {
  user: null,
  interns: [],
  selectedInternId: null,
  todayAttendance: null,
  locationPingTimer: null
};

const $ = (id) => document.getElementById(id);

const api = async (url, options = {}) => {
  const isFormData = options.body instanceof FormData;
  const response = await fetch(url, {
    credentials: 'include',
    headers: { ...(isFormData ? {} : { 'Content-Type': 'application/json' }), ...(options.headers || {}) },
    ...options
  });

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
  const coords = lat && lng ? `${Number(lat).toFixed(5)}, ${Number(lng).toFixed(5)}` : '';
  return [area, coords].filter(Boolean).join('<br>');
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
  state.interns = await api('/api/admin/interns?status=Active');
  $('internRows').innerHTML = state.interns.map((intern) => `
    <tr>
      <td>${intern.photoPath ? `<img src="${intern.photoPath}" class="avatar" alt="${intern.fullName}">` : '<span class="avatar empty">No</span>'}</td>
      <td>${intern.fullName}</td>
      <td>${intern.phoneNumber}</td>
      <td>${intern.projectName || '-'}</td>
      <td>${intern.workLocationName || '-'}</td>
      <td>${badge(intern.status)}</td>
      <td>
        <button class="ghost small" onclick="selectIntern(${intern.internId})">View</button>
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
}

async function createIntern(event) {
  event.preventDefault();
  const form = new FormData(event.target);
  const photo = form.get('photo');
  const payload = Object.fromEntries(form.entries());
  delete payload.photo;
  payload.workLatitude = payload.workLatitude ? Number(payload.workLatitude) : null;
  payload.workLongitude = payload.workLongitude ? Number(payload.workLongitude) : null;

  try {
    const created = await api('/api/admin/interns', {
      method: 'POST',
      body: JSON.stringify(payload)
    });
    if (photo && photo.size) {
      if (photo.size > 1024 * 1024) {
        alert('Intern was created, but photo was not uploaded because it is larger than 1MB.');
      } else {
        const photoForm = new FormData();
        photoForm.append('photo', photo);
        await api(`/api/admin/interns/${created.internId}/photo`, {
          method: 'POST',
          body: photoForm
        });
      }
    }
    $('credentialBox').classList.remove('hidden');
    $('credentialBox').innerHTML = `
      Login created<br>
      Username: ${created.username}<br>
      Password: ${created.temporaryPassword}
    `;
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
  state.selectedInternId = id;
  $('attendanceInternSelect').value = id;
  show('adminAttendance');
  await loadAttendance();
}

async function loadAttendance() {
  const id = $('attendanceInternSelect').value;
  const [year, month] = $('attendanceMonth').value.split('-');
  if (!id) {
    $('attendanceRows').innerHTML = '<tr><td colspan="7">Choose an intern to preview records, or export all interns.</td></tr>';
    return;
  }

  const rows = await api(`/api/admin/interns/${id}/attendance?month=${Number(month)}&year=${Number(year)}`);
  $('attendanceRows').innerHTML = rows.map(attendanceRow).join('') || '<tr><td colspan="7">No records found.</td></tr>';
}

function attendanceRow(row) {
  return `
    <tr>
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
  $('userName').textContent = profile.fullName;
  if (profile.photoPath) {
    $('internPhoto').src = profile.photoPath;
    $('internPhoto').classList.remove('hidden');
  }
  await loadToday();
  await loadInternHistory();
  await loadInternActivities();
  startAutoLocationPing();
}

async function loadToday() {
  state.todayAttendance = await api('/api/intern/attendance/today');
  const button = $('attendanceBtn');
  if (!state.todayAttendance) {
    $('todayStatus').textContent = 'Ready for clock in.';
    button.textContent = 'Clock In';
    button.classList.remove('clocked');
    button.disabled = false;
    return;
  }

  const row = state.todayAttendance;
  $('todayStatus').textContent = `${row.status} | In: ${row.clockIn || '-'} | Out: ${row.clockOut || '-'}`;
  if (row.clockIn && !row.clockOut) {
    button.textContent = 'Clock Out';
    button.classList.add('clocked');
    button.disabled = false;
    return;
  }

  button.textContent = 'Attendance Completed';
  button.classList.remove('clocked');
  button.disabled = true;
}

async function submitAttendance() {
  const button = $('attendanceBtn');
  button.disabled = true;
  button.textContent = 'Getting location...';

  try {
    const locationData = await getLocation();
    const endpoint = state.todayAttendance?.clockIn && !state.todayAttendance?.clockOut
      ? '/api/intern/attendance/clock-out'
      : '/api/intern/attendance/clock-in';
    state.todayAttendance = await api(endpoint, {
      method: 'POST',
      body: JSON.stringify(locationData)
    });
    await sendLocationPing('attendance');
    await loadToday();
    await loadInternHistory();
  } catch (error) {
    alert(error.message);
    await loadToday();
  }
}

async function sendLocationPing(source = 'manual') {
  const locationData = await getLocation();
  const ping = await api('/api/intern/location/ping', {
    method: 'POST',
    body: JSON.stringify({
      ...locationData,
      source
    })
  });
  $('lastPingStatus').textContent = `Last ping: ${ping.loggedAt} | ${ping.areaName || 'Area auto-detected'}`;
}

async function manualLocationPing() {
  const button = $('locationPingBtn');
  button.disabled = true;
  button.textContent = 'Capturing...';
  try {
    await sendLocationPing('manual');
  } catch (error) {
    alert(error.message);
  } finally {
    button.disabled = false;
    button.textContent = 'Log Current Location';
  }
}

function startAutoLocationPing() {
  if (state.locationPingTimer) {
    clearInterval(state.locationPingTimer);
  }

  const pingSilently = async () => {
    try {
      await sendLocationPing('auto');
    } catch {
      // Background ping should not block intern workflow.
    }
  };

  pingSilently();
  state.locationPingTimer = setInterval(pingSilently, 15 * 60 * 1000);
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

document.addEventListener('click', (event) => {
  const page = event.target.dataset?.page;
  if (page) {
    show(page);
    if (page === 'adminActivities') loadAdminActivities();
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
$('attendanceBtn').addEventListener('click', submitAttendance);
$('locationPingBtn').addEventListener('click', manualLocationPing);
$('internMonth').addEventListener('change', async () => {
  await loadInternHistory();
  await loadInternActivities();
});
$('activityForm').addEventListener('submit', submitActivity);
$('activityInternSelect').addEventListener('change', loadAdminActivities);
$('activityMonth').addEventListener('change', loadAdminActivities);

init();
