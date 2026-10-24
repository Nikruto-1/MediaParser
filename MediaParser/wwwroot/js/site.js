function showMessage(message, isSuccess = true) {
    let alertClass = isSuccess ? 'alert-success' : 'alert-danger';
    let alert = $('<div class="alert ' + alertClass + ' position-fixed top-0 end-0 p-3" style="z-index: 1050;">')
        .text(message)
        .appendTo('body');

    setTimeout(function () {
        alert.fadeOut('slow', function () {
            alert.remove();
        });
    }, 5000);
}
$(document).on('submit', '.ajax-form', function (event) {
    event.preventDefault();

    let form = $(this)[0];
    let formData = new FormData(form);

    $.ajax({
        type: form.getAttribute('method'),
        url: form.getAttribute('action'),
        data: formData,
        processData: false, // Відключає обробку даних
        contentType: false, // Вимикає встановлення заголовка типу контенту
        success: function (response) {
            if (response.success) {
                // Закрытие модального окна
                const modalElement = form.closest('.modal');
                if (modalElement) {
                    bootstrap.Modal.getOrCreateInstance(modalElement).hide();
                }

                if ($(form).hasClass("reload")) {
                    location.reload();
                }
                if ($(form).hasClass("deletion")) {
                    let id = $(form).find('[data-idDelete]').data('iddelete');
                    $('.media-item[data-id="' + id + '"]').fadeOut(300, function () {
                        $(this).remove();
                    });
                    $('#media_' + id).closest('.form-check').remove();
                }
                showMessage("Successful!");
            } else {
                let errorMessage = response.errorMessage || 'An unknown error occurred.';
                showMessage(errorMessage, false);
            }
        },
        error: function (xhr, status, error) {
            let errorMessage = xhr.status + ': ' + xhr.statusText;
            showMessage('Error - ' + errorMessage, false);
        }
    });
});
