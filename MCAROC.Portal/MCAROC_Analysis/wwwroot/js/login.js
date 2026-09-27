document.getElementById('password-toggle').addEventListener('click', function () {
    const password = document.getElementById('password');
    const visible = password.type === 'password';
    password.type = visible ? 'text' : 'password';
    this.textContent = visible ? 'Hide' : 'Show';
    this.setAttribute('aria-pressed', String(visible));
});
