function showMessage(message, isSuccess = true) {
    const host = document.getElementById('appNotifications') || document.body;
    const notification = document.createElement('div');
    notification.className = 'alert ' + (isSuccess ? 'alert-success' : 'alert-danger');
    notification.setAttribute('role', isSuccess ? 'status' : 'alert');
    notification.textContent = message;
    host.append(notification);
    setTimeout(() => notification.remove(), 6000);
}
$(document).on('submit', '.ajax-form', function (event) {
    event.preventDefault();
    const form = this;
    if (form.dataset.submitting === 'true') return;
    const buttons = [...form.querySelectorAll('button[type="submit"], button:not([type])')];
    form.dataset.submitting = 'true';
    form.setAttribute('aria-busy', 'true');
    buttons.forEach(button => button.disabled = true);
    $.ajax({
        type: form.getAttribute('method') || 'POST',
        url: form.getAttribute('action'),
        data: new FormData(form),
        processData: false,
        contentType: false,
        success: function (response) {
            if (!response.success) {
                showMessage(response.errorMessage || 'Не вдалося зберегти зміни. Спробуйте ще раз.', false);
                return;
            }
            const modalElement = form.closest('.modal');
            if (modalElement) bootstrap.Modal.getOrCreateInstance(modalElement).hide();
            if (form.classList.contains('reload')) { location.reload(); return; }
            if (form.classList.contains('deletion')) {
                const id = form.querySelector('[data-idDelete]')?.value;
                if (id) {
                    document.querySelectorAll('.media-item').forEach(item => {
                        if (item.dataset.id === id) item.remove();
                    });
                    document.getElementById('media_' + id)?.closest('.form-check')?.remove();
                }
                showMessage('Джерело видалено.');
            } else showMessage('Зміни збережено.');
        },
        error: function (xhr) {
            const message = xhr.status === 400
                ? 'Не вдалося перевірити форму. Оновіть сторінку й повторіть дію.'
                : 'Не вдалося зв’язатися із сервером. Спробуйте ще раз.';
            showMessage(message, false);
        },
        complete: function () {
            form.dataset.submitting = 'false';
            form.removeAttribute('aria-busy');
            buttons.forEach(button => button.disabled = false);
        }
    });
});
